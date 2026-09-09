using System.Collections.Immutable;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Domain.Review;

public static class SurvivorPolicy
{
    public const string Version = "survivor-v1";

    public static SurvivorDecision Evaluate(
        CandidateGroup group,
        ImmutableHashSet<long> eligibleOccurrenceIds,
        ImmutableHashSet<long> proposedSelectedOccurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(eligibleOccurrenceIds);
        ArgumentNullException.ThrowIfNull(proposedSelectedOccurrenceIds);

        var membersById = new Dictionary<long, ScanFileOccurrence>(group.Members.Length);
        foreach (var member in group.Members)
        {
            if (!membersById.TryAdd(member.Token.OccurrenceId, member))
            {
                return Decision(
                    SurvivorDecisionKind.InconsistentEvidence,
                    SurvivorUnavailableReason.InconsistentFacts);
            }
        }

        foreach (var selectedId in proposedSelectedOccurrenceIds)
        {
            if (!eligibleOccurrenceIds.Contains(selectedId)
                || !membersById.ContainsKey(selectedId))
            {
                return Decision(
                    SurvivorDecisionKind.IneligibleSelection,
                    SurvivorUnavailableReason.IneligibleOccurrence);
            }
        }

        var eligibleMembers = group.Members
            .Where(member => eligibleOccurrenceIds.Contains(member.Token.OccurrenceId))
            .ToArray();
        if (eligibleMembers.Any(member => member.PhysicalFileKey is null))
        {
            return Decision(
                SurvivorDecisionKind.InconsistentEvidence,
                SurvivorUnavailableReason.InconsistentFacts);
        }

        var remainingKnownPaths = 0;
        var remainingPhysicalKeys = new HashSet<PhysicalFileKey>();
        var externalSurvivors = ImmutableArray.CreateBuilder<ExternalSurvivor>();
        var hasUnknownLinks = false;
        var hasInconsistentFacts = false;

        foreach (var physicalGroup in eligibleMembers.GroupBy(member => member.PhysicalFileKey!.Value))
        {
            var observations = physicalGroup.ToArray();
            var pathGroups = observations.GroupBy(
                member => member.ResolvedPath,
                StringComparer.OrdinalIgnoreCase).ToArray();
            var currentPathCount = group.Members
                .Where(member => member.PhysicalFileKey == physicalGroup.Key)
                .Select(member => member.ResolvedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var knownPathsForKey = 0;
            foreach (var pathGroup in pathGroups)
            {
                if (pathGroup.Any(member =>
                    proposedSelectedOccurrenceIds.Contains(member.Token.OccurrenceId)))
                {
                    continue;
                }

                knownPathsForKey++;
            }

            remainingKnownPaths += knownPathsForKey;
            if (knownPathsForKey > 0)
            {
                remainingPhysicalKeys.Add(physicalGroup.Key);
            }

            var linkEvidence = EvaluateLinkEvidence(observations, currentPathCount);
            switch (linkEvidence.Kind)
            {
                case LinkEvidenceKind.External:
                    externalSurvivors.Add(
                        new ExternalSurvivor(
                            physicalGroup.Key,
                            linkEvidence.LinkCount,
                            currentPathCount));
                    remainingPhysicalKeys.Add(physicalGroup.Key);
                    break;
                case LinkEvidenceKind.Unknown:
                    hasUnknownLinks = true;
                    break;
                case LinkEvidenceKind.Inconsistent:
                    hasInconsistentFacts = true;
                    break;
            }
        }

        var orderedExternalSurvivors = externalSurvivors
            .OrderBy(survivor => survivor.PhysicalFileKey.VolumeSerial)
            .ThenBy(survivor => survivor.PhysicalFileKey.FileId.High)
            .ThenBy(survivor => survivor.PhysicalFileKey.FileId.Low)
            .ToImmutableArray();

        if (remainingKnownPaths > 0 || !orderedExternalSurvivors.IsEmpty)
        {
            return new SurvivorDecision(
                SurvivorDecisionKind.Allowed,
                SurvivorUnavailableReason.None,
                remainingKnownPaths,
                remainingPhysicalKeys.Count,
                orderedExternalSurvivors);
        }

        if (hasInconsistentFacts)
        {
            return Decision(
                SurvivorDecisionKind.InconsistentEvidence,
                SurvivorUnavailableReason.InconsistentFacts);
        }

        return Decision(
            SurvivorDecisionKind.OverrideRequired,
            hasUnknownLinks
                ? SurvivorUnavailableReason.UnknownLinks
                : SurvivorUnavailableReason.NoProvenSurvivor);
    }

    private static LinkEvidence EvaluateLinkEvidence(
        ScanFileOccurrence[] observations,
        int knownPathCount)
    {
        var usableKnownLinkCounts = observations
            .Where(member => member.Allocation.Stable)
            .Select(member => member.Allocation.LinkCount)
            .Where(linkCount => linkCount is not null)
            .Select(linkCount => linkCount!.Value)
            .Distinct()
            .ToArray();
        if (usableKnownLinkCounts.Length > 1
            || usableKnownLinkCounts.Any(linkCount => linkCount == 0 || linkCount < knownPathCount))
        {
            return new LinkEvidence(LinkEvidenceKind.Inconsistent, 0);
        }

        if (observations.Any(member => !member.Allocation.Stable)
            || observations.Any(member => member.Allocation.LinkCount is null))
        {
            return new LinkEvidence(LinkEvidenceKind.Unknown, 0);
        }

        var linkCount = usableKnownLinkCounts[0];

        return linkCount > knownPathCount
            ? new LinkEvidence(LinkEvidenceKind.External, linkCount)
            : new LinkEvidence(LinkEvidenceKind.Complete, linkCount);
    }

    private static SurvivorDecision Decision(
        SurvivorDecisionKind kind,
        SurvivorUnavailableReason reason) =>
        new(kind, reason, 0, 0, ImmutableArray<ExternalSurvivor>.Empty);

    private enum LinkEvidenceKind
    {
        Complete,
        External,
        Unknown,
        Inconsistent,
    }

    private readonly record struct LinkEvidence(LinkEvidenceKind Kind, uint LinkCount);
}
