using System.Collections.Immutable;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Domain.Review;

public static class SelectionAccounting
{
    public static SelectionRecovery Calculate(
        CandidateGroup group,
        ImmutableHashSet<long> eligibleOccurrenceIds,
        ImmutableHashSet<long> selectedOccurrenceIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(eligibleOccurrenceIds);
        ArgumentNullException.ThrowIfNull(selectedOccurrenceIds);

        if (selectedOccurrenceIds.Count == 0)
        {
            return Available(0);
        }

        var membersById = new Dictionary<long, ScanFileOccurrence>(group.Members.Length);
        foreach (var member in group.Members)
        {
            if (!membersById.TryAdd(member.Token.OccurrenceId, member))
            {
                return Unavailable(SelectionRecoveryUnavailableReason.InconsistentFacts);
            }
        }

        var selectedKeys = new HashSet<PhysicalFileKey>();
        foreach (var selectedId in selectedOccurrenceIds)
        {
            if (!eligibleOccurrenceIds.Contains(selectedId)
                || !membersById.TryGetValue(selectedId, out var member)
                || member.PhysicalFileKey is not { } key)
            {
                return Unavailable(SelectionRecoveryUnavailableReason.InconsistentFacts);
            }

            selectedKeys.Add(key);
        }

        var recoverablePhysicalAllocations = new List<long>(selectedKeys.Count);
        foreach (var key in selectedKeys)
        {
            var currentMembers = group.Members
                .Where(member => member.PhysicalFileKey == key)
                .ToArray();
            var currentPaths = currentMembers
                .GroupBy(member => member.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (currentPaths.Any(pathGroup =>
                !pathGroup.Any(member => selectedOccurrenceIds.Contains(member.Token.OccurrenceId))))
            {
                continue;
            }

            var eligibleObservations = currentMembers
                .Where(member => eligibleOccurrenceIds.Contains(member.Token.OccurrenceId))
                .ToArray();
            var evidence = EvaluateEvidence(eligibleObservations, currentPaths.Length);
            switch (evidence.Kind)
            {
                case RecoveryEvidenceKind.External:
                    continue;
                case RecoveryEvidenceKind.Recoverable:
                    recoverablePhysicalAllocations.Add(evidence.AllocatedBytes);
                    break;
                case RecoveryEvidenceKind.UnknownAllocation:
                    return Unavailable(SelectionRecoveryUnavailableReason.UnknownAllocation);
                case RecoveryEvidenceKind.UnknownLinks:
                    return Unavailable(SelectionRecoveryUnavailableReason.UnknownLinks);
                default:
                    return Unavailable(SelectionRecoveryUnavailableReason.InconsistentFacts);
            }
        }

        try
        {
            long total = 0;
            foreach (var allocation in recoverablePhysicalAllocations)
            {
                total = checked(total + allocation);
            }

            return Available(total);
        }
        catch (OverflowException)
        {
            return Unavailable(SelectionRecoveryUnavailableReason.Overflow);
        }
    }

    private static RecoveryEvidence EvaluateEvidence(
        ScanFileOccurrence[] observations,
        int knownPathCount)
    {
        if (observations.Length == 0 || observations.Any(member => !member.Allocation.Stable))
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.Inconsistent, 0);
        }

        var knownLinkCounts = observations
            .Select(member => member.Allocation.LinkCount)
            .Where(linkCount => linkCount is not null)
            .Select(linkCount => linkCount!.Value)
            .Distinct()
            .ToArray();
        if (knownLinkCounts.Length > 1 || knownLinkCounts.Any(linkCount => linkCount == 0))
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.Inconsistent, 0);
        }

        if (knownLinkCounts.Any(linkCount => linkCount < knownPathCount))
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.Inconsistent, 0);
        }

        if (observations.Any(member => member.Allocation.LinkCount is null))
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.UnknownLinks, 0);
        }

        if (knownLinkCounts[0] > knownPathCount)
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.External, 0);
        }

        var knownAllocations = observations
            .Select(member => member.Allocation.AllocatedBytes)
            .Where(allocatedBytes => allocatedBytes is not null)
            .Select(allocatedBytes => allocatedBytes!.Value)
            .Distinct()
            .ToArray();
        if (knownAllocations.Length > 1 || knownAllocations.Any(allocation => allocation < 0))
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.Inconsistent, 0);
        }

        if (observations.Any(member => member.Allocation.AllocatedBytes is null))
        {
            return new RecoveryEvidence(RecoveryEvidenceKind.UnknownAllocation, 0);
        }

        return new RecoveryEvidence(RecoveryEvidenceKind.Recoverable, knownAllocations[0]);
    }

    private static SelectionRecovery Available(long bytes) =>
        new(bytes, SelectionRecoveryUnavailableReason.None);

    private static SelectionRecovery Unavailable(SelectionRecoveryUnavailableReason reason) =>
        new(null, reason);

    private enum RecoveryEvidenceKind
    {
        Recoverable,
        External,
        UnknownAllocation,
        UnknownLinks,
        Inconsistent,
    }

    private readonly record struct RecoveryEvidence(
        RecoveryEvidenceKind Kind,
        long AllocatedBytes);
}
