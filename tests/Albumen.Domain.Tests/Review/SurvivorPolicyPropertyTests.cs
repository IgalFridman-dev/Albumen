using System.Collections.Immutable;
using System.Globalization;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Review;

public sealed class SurvivorPolicyPropertyTests
{
    private const int Seed = 0x51F7;

    [Fact]
    public void GeneratedAllowedDecisionsAlwaysContainAProvenSurvivor()
    {
        var random = new Random(Seed);

        for (var memberCount = 1; memberCount <= 300; memberCount++)
        {
            var propertyCase = Generate(random, memberCount);
            var decision = SurvivorPolicy.Evaluate(
                propertyCase.Group,
                propertyCase.Eligible,
                propertyCase.Proposed);

            if (decision.Kind is not SurvivorDecisionKind.Allowed)
            {
                continue;
            }

            var hasProvenOccurrence = decision.RemainingKnownPathOccurrences > 0
                || decision.ExternalSurvivors.Length > 0;
            if (!hasProvenOccurrence || decision.RemainingPhysicalKeys <= 0)
            {
                var shrunk = Shrink(propertyCase, ViolatesAllowedInvariant);
                Assert.Fail(
                    $"Seed {Seed} violated the allowed-survivor property. {Describe(shrunk)}");
            }

            Assert.All(
                decision.ExternalSurvivors,
                survivor => Assert.True(survivor.LinkCount > survivor.KnownPathCount));
        }
    }

    [Fact]
    public void GeneratedCompleteSetDecisionsMatchAnIndependentSurvivorOracle()
    {
        var random = new Random(Seed);

        for (var memberCount = 1; memberCount <= 300; memberCount++)
        {
            var propertyCase = Generate(random, memberCount);
            var decision = SurvivorPolicy.Evaluate(
                propertyCase.Group,
                propertyCase.Eligible,
                propertyCase.Proposed);
            var expectedAllowed = HasIndependentlyProvenSurvivor(propertyCase);
            if ((decision.Kind is SurvivorDecisionKind.Allowed) != expectedAllowed)
            {
                var shrunk = Shrink(propertyCase, ViolatesIndependentOracle);
                Assert.Fail(
                    $"Seed {Seed} disagreed with the independent survivor oracle. "
                    + $"Expected allowed={expectedAllowed}; actual={decision.Kind}. {Describe(shrunk)}");
            }
        }
    }

    [Fact]
    public void RepeatedEvaluationOfGeneratedSingleAndBulkSetsIsStable()
    {
        var random = new Random(Seed);

        for (var memberCount = 1; memberCount <= 300; memberCount++)
        {
            var propertyCase = Generate(random, memberCount);
            var first = SurvivorPolicy.Evaluate(
                propertyCase.Group,
                propertyCase.Eligible,
                propertyCase.Proposed);
            var repeated = SurvivorPolicy.Evaluate(
                propertyCase.Group,
                propertyCase.Eligible,
                propertyCase.Proposed);

            Assert.Equal(first.Kind, repeated.Kind);
            Assert.Equal(first.Reason, repeated.Reason);
            Assert.Equal(first.RemainingKnownPathOccurrences, repeated.RemainingKnownPathOccurrences);
            Assert.Equal(first.RemainingPhysicalKeys, repeated.RemainingPhysicalKeys);
            Assert.Equal(first.ExternalSurvivors.ToArray(), repeated.ExternalSurvivors.ToArray());
        }
    }

    private static PropertyCase Generate(Random random, int memberCount)
    {
        if (memberCount == 2)
        {
            var key = ReviewGroups.Key(1);
            var thresholdGroup = ReviewGroups.Group(
                ReviewGroups.Occurrence(1, key, @"C:\scope\eligible.jpg", 4096, 2),
                ReviewGroups.Occurrence(2, key, @"C:\scope\pending.jpg", 4096, 2));
            return new PropertyCase(
                thresholdGroup,
                ImmutableHashSet.Create(1L),
                ImmutableHashSet.Create(1L));
        }

        var members = ImmutableArray.CreateBuilder<ScanFileOccurrence>(memberCount);
        var keyCount = Math.Min(memberCount, random.Next(1, 7));
        var keyLinkCounts = new uint?[keyCount];
        var keyStability = new bool[keyCount];

        for (var keyIndex = 0; keyIndex < keyCount; keyIndex++)
        {
            keyLinkCounts[keyIndex] = random.Next(5) switch
            {
                0 => null,
                1 => 0,
                _ => (uint)random.Next(1, memberCount + 4),
            };
            keyStability[keyIndex] = random.Next(4) != 0;
        }

        for (var index = 0; index < memberCount; index++)
        {
            var keyIndex = index % keyCount;
            var pathNumber = random.Next(5) == 0 ? index / 2 : index;
            var path = $@"C:\scope\key-{keyIndex}\photo-{pathNumber}.jpg";
            if (random.Next(7) == 0)
            {
                path = path.ToUpperInvariant();
            }

            var linkCount = keyLinkCounts[keyIndex];
            var stable = keyStability[keyIndex];
            if (random.Next(4) == 0)
            {
                linkCount = random.Next(4) switch
                {
                    0 => null,
                    1 => 0,
                    _ => (uint)random.Next(1, memberCount + 4),
                };
            }

            if (random.Next(6) == 0)
            {
                stable = !stable;
            }

            members.Add(ReviewGroups.Occurrence(
                index + 1,
                ReviewGroups.Key((ulong)keyIndex + 1),
                path,
                allocatedBytes: random.Next(5) == 0 ? null : random.NextInt64(0, 1_000_000),
                linkCount,
                stable));
        }

        var group = ReviewGroups.Group([.. members]);
        var eligible = group.Members
            .Where(_ => random.Next(6) != 0)
            .Select(member => member.Token.OccurrenceId)
            .ToImmutableHashSet();
        if (eligible.Count == 0)
        {
            eligible = eligible.Add(group.Members[0].Token.OccurrenceId);
        }

        var proposed = eligible
            .Where(_ => random.Next(2) == 0)
            .ToImmutableHashSet();

        return new PropertyCase(group, eligible, proposed);
    }

    private static bool HasIndependentlyProvenSurvivor(PropertyCase propertyCase)
    {
        var eligibleMembers = propertyCase.Group.Members
            .Where(member => propertyCase.Eligible.Contains(member.Token.OccurrenceId))
            .ToArray();
        var knownSurvivor = eligibleMembers
            .GroupBy(member => member.PhysicalFileKey!.Value)
            .SelectMany(members => members.GroupBy(
                member => member.ResolvedPath,
                StringComparer.OrdinalIgnoreCase))
            .Any(path => !path.Any(member =>
                propertyCase.Proposed.Contains(member.Token.OccurrenceId)));
        if (knownSurvivor)
        {
            return true;
        }

        foreach (var eligibleKey in eligibleMembers.GroupBy(member => member.PhysicalFileKey!.Value))
        {
            var evidence = eligibleKey.Select(member => member.Allocation).ToArray();
            if (evidence.Length == 0
                || evidence.Any(allocation => !allocation.Stable || allocation.LinkCount is null))
            {
                continue;
            }

            var observedLinkCount = evidence[0].LinkCount!.Value;
            var currentPathCount = propertyCase.Group.Members
                .Where(member => member.PhysicalFileKey == eligibleKey.Key)
                .Select(member => member.ResolvedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (observedLinkCount > 0
                && evidence.All(allocation => allocation.LinkCount == observedLinkCount)
                && observedLinkCount > currentPathCount)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ViolatesAllowedInvariant(PropertyCase propertyCase)
    {
        try
        {
            var decision = SurvivorPolicy.Evaluate(
                propertyCase.Group,
                propertyCase.Eligible,
                propertyCase.Proposed);
            return decision.Kind is SurvivorDecisionKind.Allowed
                && (decision.RemainingPhysicalKeys <= 0
                    || (decision.RemainingKnownPathOccurrences <= 0
                        && decision.ExternalSurvivors.IsEmpty));
        }
        catch
        {
            return true;
        }
    }

    private static bool ViolatesIndependentOracle(PropertyCase propertyCase)
    {
        try
        {
            var decision = SurvivorPolicy.Evaluate(
                propertyCase.Group,
                propertyCase.Eligible,
                propertyCase.Proposed);
            return (decision.Kind is SurvivorDecisionKind.Allowed)
                != HasIndependentlyProvenSurvivor(propertyCase);
        }
        catch
        {
            return true;
        }
    }

    private static PropertyCase Shrink(PropertyCase original, Func<PropertyCase, bool> stillFails)
    {
        var current = original;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var proposedId in current.Proposed.OrderDescending())
            {
                var candidate = current with { Proposed = current.Proposed.Remove(proposedId) };
                if (stillFails(candidate))
                {
                    current = candidate;
                    changed = true;
                }
            }

            foreach (var eligibleId in current.Eligible
                         .Except(current.Proposed)
                         .OrderDescending())
            {
                var candidate = current with { Eligible = current.Eligible.Remove(eligibleId) };
                if (stillFails(candidate))
                {
                    current = candidate;
                    changed = true;
                }
            }

            for (var index = current.Group.Members.Length - 1; index >= 0; index--)
            {
                var candidateMembers = current.Group.Members.RemoveAt(index);
                if (candidateMembers.IsEmpty)
                {
                    continue;
                }

                var retainedIds = candidateMembers
                    .Select(member => member.Token.OccurrenceId)
                    .ToImmutableHashSet();
                var candidate = new PropertyCase(
                    current.Group with { Members = candidateMembers },
                    current.Eligible.Intersect(retainedIds),
                    current.Proposed.Intersect(retainedIds));
                if (stillFails(candidate))
                {
                    current = candidate;
                    changed = true;
                }
            }
        }

        return current;
    }

    private static string Describe(PropertyCase propertyCase) =>
        $"group=[{string.Join(",", propertyCase.Group.Members.Select(member =>
            $"{member.Token.OccurrenceId}:{member.PhysicalFileKey}:{member.ResolvedPath}:"
            + $"stable={member.Allocation.Stable}:links="
            + $"{member.Allocation.LinkCount?.ToString(CultureInfo.InvariantCulture) ?? "null"}"))}] "
        + $"eligible=[{string.Join(",", propertyCase.Eligible.Order())}] "
        + $"proposed=[{string.Join(",", propertyCase.Proposed.Order())}]";

    private sealed record PropertyCase(
        CandidateGroup Group,
        ImmutableHashSet<long> Eligible,
        ImmutableHashSet<long> Proposed);
}
