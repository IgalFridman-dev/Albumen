using System.Collections.Immutable;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Review;

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
            var expectedAllowed = HasKnownSurvivor(propertyCase) || HasStableExternalSurvivor(propertyCase);

            if (expectedAllowed)
            {
                Assert.Equal(SurvivorDecisionKind.Allowed, decision.Kind);
            }
            else
            {
                Assert.NotEqual(SurvivorDecisionKind.Allowed, decision.Kind);
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

            members.Add(ReviewGroups.Occurrence(
                index + 1,
                ReviewGroups.Key((ulong)keyIndex + 1),
                path,
                allocatedBytes: random.Next(5) == 0 ? null : random.NextInt64(0, 1_000_000),
                linkCount: keyLinkCounts[keyIndex],
                stable: keyStability[keyIndex]));
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

    private static bool HasKnownSurvivor(PropertyCase propertyCase) =>
        EligiblePathGroups(propertyCase)
            .Any(pathGroup => !pathGroup.Any(member => propertyCase.Proposed.Contains(member.Token.OccurrenceId)));

    private static bool HasStableExternalSurvivor(PropertyCase propertyCase) =>
        propertyCase.Group.Members
            .Where(member => propertyCase.Eligible.Contains(member.Token.OccurrenceId))
            .GroupBy(member => member.PhysicalFileKey!.Value)
            .Any(members =>
            {
                var materialized = members.ToArray();
                var distinctPaths = materialized
                    .Select(member => member.ResolvedPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var first = materialized[0].Allocation;
                return first.Stable
                    && first.LinkCount is > 0
                    && materialized.All(member =>
                        member.Allocation.Stable
                        && member.Allocation.LinkCount == first.LinkCount)
                    && first.LinkCount > distinctPaths;
            });

    private static IEnumerable<IGrouping<string, ScanFileOccurrence>> EligiblePathGroups(
        PropertyCase propertyCase) =>
        propertyCase.Group.Members
            .Where(member => propertyCase.Eligible.Contains(member.Token.OccurrenceId))
            .GroupBy(member => member.ResolvedPath, StringComparer.OrdinalIgnoreCase);

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

    private static PropertyCase Shrink(PropertyCase original, Func<PropertyCase, bool> stillFails)
    {
        var current = original;
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
            }
        }

        return current;
    }

    private static string Describe(PropertyCase propertyCase) =>
        $"group=[{string.Join(",", propertyCase.Group.Members.Select(member => member.Token.OccurrenceId))}] "
        + $"eligible=[{string.Join(",", propertyCase.Eligible.Order())}] "
        + $"proposed=[{string.Join(",", propertyCase.Proposed.Order())}]";

    private sealed record PropertyCase(
        CandidateGroup Group,
        ImmutableHashSet<long> Eligible,
        ImmutableHashSet<long> Proposed);
}
