using System.Collections.Immutable;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;

namespace Albumen.Domain.Action;

public static class ActionSafetyPolicy
{
    public const string Version = "action-safety-v1";

    public static ActionSafetyDecision Evaluate(ActionSafetyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.ApprovedGroup);
        ArgumentNullException.ThrowIfNull(input.FreshEligibleOccurrenceIds);
        ArgumentNullException.ThrowIfNull(input.RemainingApprovedOccurrenceIds);
        ArgumentNullException.ThrowIfNull(input.OverrideApprovedOccurrenceIds);

        if (input.CurrentOccurrences.IsDefault)
        {
            throw new ArgumentException(
                "Current occurrences must be initialized.",
                nameof(input));
        }

        if (input.ApprovedGroup.Members.IsDefault)
        {
            throw new ArgumentException(
                "Approved group members must be initialized.",
                nameof(input));
        }

        var authority = ValidateAuthority(input);
        if (authority is not null)
        {
            return authority;
        }

        if (input.HasDependentUnknown)
        {
            return Decision(
                ActionSafetyDecisionKind.UnsafeCurrentFacts,
                ActionSafetyReason.DependentOutcomeUnknown);
        }

        var currentFacts = ActionCurrentFactGuard.Validate(input.CurrentOccurrences);
        if (!currentFacts.IsSafe)
        {
            return new ActionSafetyDecision(
                ActionSafetyDecisionKind.UnsafeCurrentFacts,
                currentFacts.Reason,
                currentFacts.Survivor);
        }

        var currentGroup = input.ApprovedGroup with
        {
            Members = input.CurrentOccurrences,
        };
        var survivor = SurvivorPolicy.Evaluate(
            currentGroup,
            input.FreshEligibleOccurrenceIds,
            input.RemainingApprovedOccurrenceIds);

        if (survivor.Kind is SurvivorDecisionKind.Allowed)
        {
            return new ActionSafetyDecision(
                ActionSafetyDecisionKind.Allowed,
                ActionSafetyReason.None,
                survivor);
        }

        var covered = input.OverrideEvidenceExact
            && input.RemainingApprovedOccurrenceIds.IsSubsetOf(
                input.OverrideApprovedOccurrenceIds);
        return survivor.Kind is SurvivorDecisionKind.OverrideRequired && covered
            ? new ActionSafetyDecision(
                ActionSafetyDecisionKind.Allowed,
                ActionSafetyReason.None,
                survivor)
            : new ActionSafetyDecision(
                ActionSafetyDecisionKind.UnsafeCurrentFacts,
                ActionSafetyReason.NoCurrentSurvivor,
                survivor);
    }

    private static ActionSafetyDecision? ValidateAuthority(ActionSafetyInput input)
    {
        var approved = input.ApprovedGroup;
        if (approved.GroupId == Guid.Empty || approved.Pin.ScanId == Guid.Empty)
        {
            return Decision(ActionSafetyDecisionKind.StaleAuthority, ActionSafetyReason.StalePin);
        }

        var approvedById = new Dictionary<long, ScanFileOccurrence>(approved.Members.Length);
        foreach (var occurrence in approved.Members)
        {
            if (occurrence is null
                || occurrence.Token.Pin != approved.Pin
                || !approvedById.TryAdd(occurrence.Token.OccurrenceId, occurrence))
            {
                return Decision(ActionSafetyDecisionKind.StaleAuthority, ActionSafetyReason.StalePin);
            }
        }

        if (!input.FreshEligibleOccurrenceIds.IsSubsetOf(approvedById.Keys)
            || !input.RemainingApprovedOccurrenceIds.IsSubsetOf(approvedById.Keys)
            || !input.OverrideApprovedOccurrenceIds.IsSubsetOf(approvedById.Keys))
        {
            return Decision(ActionSafetyDecisionKind.StaleAuthority, ActionSafetyReason.StalePin);
        }

        var currentIds = new HashSet<long>();
        foreach (var current in input.CurrentOccurrences)
        {
            if (current is null)
            {
                throw new ArgumentException(
                    "Current occurrences cannot contain null values.",
                    nameof(input));
            }

            if (!currentIds.Add(current.Token.OccurrenceId))
            {
                return Decision(
                    ActionSafetyDecisionKind.UnsafeCurrentFacts,
                    ActionSafetyReason.InconsistentLinks);
            }

            if (!approvedById.TryGetValue(current.Token.OccurrenceId, out var approvedOccurrence)
                || current.Token != approvedOccurrence.Token)
            {
                return Decision(ActionSafetyDecisionKind.StaleAuthority, ActionSafetyReason.StalePin);
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    current.DiscoveredPath,
                    approvedOccurrence.DiscoveredPath)
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    current.ResolvedPath,
                    approvedOccurrence.ResolvedPath))
            {
                return Decision(ActionSafetyDecisionKind.UnsafeCurrentFacts, ActionSafetyReason.Changed);
            }

            if (current.Exact.State is not ScanStageState.Succeeded)
            {
                return Decision(
                    ActionSafetyDecisionKind.UnsafeCurrentFacts,
                    ReasonFor(current.Exact.Reason));
            }

            if (current.PhysicalFileKey is null
                || current.Content is null
                || current.PhysicalFileKey != approvedOccurrence.PhysicalFileKey
                || current.Content != approvedOccurrence.Content)
            {
                return Decision(ActionSafetyDecisionKind.UnsafeCurrentFacts, ActionSafetyReason.Changed);
            }
        }

        return input.RemainingApprovedOccurrenceIds.IsSubsetOf(currentIds)
            ? null
            : Decision(ActionSafetyDecisionKind.UnsafeCurrentFacts, ActionSafetyReason.Missing);
    }

    private static ActionSafetyReason ReasonFor(ScanReason reason) => reason switch
    {
        ScanReason.Missing => ActionSafetyReason.Missing,
        ScanReason.Changed => ActionSafetyReason.Changed,
        ScanReason.AccessDenied => ActionSafetyReason.AccessDenied,
        ScanReason.SharingViolation => ActionSafetyReason.SharingViolation,
        ScanReason.UnsafeLocation => ActionSafetyReason.UnsafeLocation,
        _ => ActionSafetyReason.UnknownSafety,
    };

    private static ActionSafetyDecision Decision(
        ActionSafetyDecisionKind kind,
        ActionSafetyReason reason) =>
        new(kind, reason, ActionCurrentFactGuard.EmptySurvivor);
}

internal static class ActionCurrentFactGuard
{
    internal static readonly SurvivorDecision EmptySurvivor = new(
        SurvivorDecisionKind.InconsistentEvidence,
        SurvivorUnavailableReason.InconsistentFacts,
        RemainingKnownPathOccurrences: 0,
        RemainingPhysicalKeys: 0,
        ImmutableArray<ExternalSurvivor>.Empty);

    internal static CurrentFactGuardResult Validate(
        ImmutableArray<ScanFileOccurrence> currentOccurrences)
    {
        foreach (var occurrence in currentOccurrences)
        {
            if (!occurrence.Allocation.Stable
                || occurrence.Allocation.AllocatedBytes is null or <= 0
                || occurrence.Allocation.LinkCount is null or 0)
            {
                return Unsafe();
            }
        }

        foreach (var pathGroup in currentOccurrences.GroupBy(
                     occurrence => occurrence.ResolvedPath,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (pathGroup
                .Select(occurrence => occurrence.PhysicalFileKey!.Value)
                .Distinct()
                .Skip(1)
                .Any())
            {
                return Unsafe();
            }
        }

        foreach (var physicalGroup in currentOccurrences.GroupBy(
                     occurrence => occurrence.PhysicalFileKey!.Value))
        {
            var observations = physicalGroup.ToArray();
            var first = observations[0];
            if (observations.Any(occurrence =>
                    occurrence.Allocation.AllocatedBytes != first.Allocation.AllocatedBytes
                    || occurrence.Allocation.LinkCount != first.Allocation.LinkCount))
            {
                return Unsafe();
            }

            var knownPathCount = observations
                .Select(occurrence => occurrence.ResolvedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .LongCount();
            if (knownPathCount > first.Allocation.LinkCount!.Value)
            {
                return Unsafe();
            }
        }

        return new CurrentFactGuardResult(
            IsSafe: true,
            ActionSafetyReason.None,
            EmptySurvivor);
    }

    private static CurrentFactGuardResult Unsafe() => new(
        IsSafe: false,
        ActionSafetyReason.InconsistentLinks,
        EmptySurvivor);
}

internal sealed record CurrentFactGuardResult(
    bool IsSafe,
    ActionSafetyReason Reason,
    SurvivorDecision Survivor);
