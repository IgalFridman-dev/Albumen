using System.Collections.Immutable;

namespace Albumen.Domain.Scan;

public static class ExactAccounting
{
    public static PotentialRecovery Calculate(ImmutableArray<ScanFileOccurrence> members)
    {
        if (members.IsDefaultOrEmpty)
        {
            return Available(0);
        }

        var observations = new Dictionary<PhysicalFileKey, PhysicalObservation>();

        foreach (var member in members)
        {
            if (member.PhysicalFileKey is not { } key
                || member.Content is not { } content
                || content.PhysicalFileKey != key
                || !member.Allocation.Stable
                || string.IsNullOrWhiteSpace(member.ResolvedPath))
            {
                return Unavailable(RecoveryUnavailableReason.InconsistentFacts);
            }

            if (member.Allocation.AllocatedBytes is not { } allocatedBytes)
            {
                return Unavailable(RecoveryUnavailableReason.UnknownAllocation);
            }

            if (member.Allocation.LinkCount is not { } linkCount)
            {
                return Unavailable(RecoveryUnavailableReason.UnknownLinks);
            }

            if (allocatedBytes < 0 || linkCount == 0)
            {
                return Unavailable(RecoveryUnavailableReason.InconsistentFacts);
            }

            if (!observations.TryGetValue(key, out var observation))
            {
                observations.Add(
                    key,
                    new PhysicalObservation(content, allocatedBytes, linkCount, member.ResolvedPath));
                continue;
            }

            if (observation.Content != content
                || observation.AllocatedBytes != allocatedBytes
                || observation.LinkCount != linkCount)
            {
                return Unavailable(RecoveryUnavailableReason.InconsistentFacts);
            }

            observation.Paths.Add(member.ResolvedPath);
        }

        var allocations = new List<long>(observations.Count);
        foreach (var observation in observations.Values)
        {
            var observedLinkCount = (ulong)observation.Paths.Count;
            if (observedLinkCount > observation.LinkCount)
            {
                return Unavailable(RecoveryUnavailableReason.InconsistentFacts);
            }

            if (observedLinkCount < observation.LinkCount)
            {
                return Unavailable(RecoveryUnavailableReason.ExternalLinks);
            }

            allocations.Add(observation.AllocatedBytes);
        }

        try
        {
            long total = 0;
            long smallest = long.MaxValue;
            foreach (var allocation in allocations)
            {
                total = checked(total + allocation);
                smallest = Math.Min(smallest, allocation);
            }

            return Available(allocations.Count == 0 ? 0 : checked(total - smallest));
        }
        catch (OverflowException)
        {
            return Unavailable(RecoveryUnavailableReason.Overflow);
        }
    }

    private static PotentialRecovery Available(long bytes) =>
        new(bytes, RecoveryUnavailableReason.None);

    private static PotentialRecovery Unavailable(RecoveryUnavailableReason reason) =>
        new(null, reason);

    private sealed class PhysicalObservation
    {
        public PhysicalObservation(
            ContentRevision content,
            long allocatedBytes,
            uint linkCount,
            string firstPath)
        {
            Content = content;
            AllocatedBytes = allocatedBytes;
            LinkCount = linkCount;
            Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { firstPath };
        }

        public ContentRevision Content { get; }

        public long AllocatedBytes { get; }

        public uint LinkCount { get; }

        public HashSet<string> Paths { get; }
    }
}
