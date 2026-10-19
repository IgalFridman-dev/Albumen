using System.Collections.Immutable;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Action;

public sealed class ActionSafetyPropertyTests
{
    [Fact]
    public void AllowedAlwaysHasACurrentSurvivorOrExactRemainingOverrideCoverage()
    {
        var random = new Random(0x1A5);

        for (var iteration = 0; iteration < 300; iteration++)
        {
            var memberCount = random.Next(1, 8);
            var members = Enumerable.Range(1, memberCount)
                .Select(index => ActionGroups.Occurrence(
                    index,
                    ActionGroups.Key((ulong)random.Next(1, 4)),
                    $@"C:\scope\{index}.jpg",
                    allocatedBytes: 4096,
                    linkCount: (uint)memberCount,
                    stable: true))
                .ToArray();
            var approved = ActionGroups.Group(members);
            var remaining = members
                .Where(_ => random.Next(2) == 0)
                .Select(item => item.Token.OccurrenceId)
                .ToImmutableHashSet();
            var overrideApproved = members
                .Where(_ => random.Next(2) == 0)
                .Select(item => item.Token.OccurrenceId)
                .ToImmutableHashSet();
            var overrideExact = random.Next(2) == 0;
            var input = ActionGroups.Input(
                approved,
                approved.Members,
                remaining,
                overrideApproved,
                overrideExact);

            var result = ActionSafetyPolicy.Evaluate(input);
            if (result.Kind is not ActionSafetyDecisionKind.Allowed)
            {
                continue;
            }

            var currentGroup = approved with { Members = approved.Members };
            var survivor = SurvivorPolicy.Evaluate(
                currentGroup,
                approved.Members.Select(item => item.Token.OccurrenceId).ToImmutableHashSet(),
                remaining);
            var justified = survivor.Kind is SurvivorDecisionKind.Allowed
                || survivor.Kind is SurvivorDecisionKind.OverrideRequired
                    && overrideExact
                    && remaining.IsSubsetOf(overrideApproved);

            Assert.True(justified, $"Unjustified Allowed decision at iteration {iteration}.");
        }
    }

    [Fact]
    public void UnknownEvidenceNeverSatisfiesEitherAllowBranch()
    {
        var random = new Random(0xBAD);

        for (var iteration = 0; iteration < 100; iteration++)
        {
            var linkCount = random.Next(2) == 0 ? null : (uint?)random.Next(1, 5);
            var stable = linkCount is not null && random.Next(2) == 0;
            if (linkCount is not null && stable)
            {
                linkCount = null;
            }

            var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
            var current = ImmutableArray.Create(
                ActionGroups.RefreshFacts(
                    approved.Members[0],
                    approved.Members[0].Allocation.AllocatedBytes,
                    linkCount,
                    stable));
            var input = ActionGroups.Input(
                approved,
                current,
                remainingApproved: [1],
                overrideApproved: [1],
                overrideExact: true);

            var result = ActionSafetyPolicy.Evaluate(input);

            Assert.NotEqual(ActionSafetyDecisionKind.Allowed, result.Kind);
            Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
        }
    }
}
