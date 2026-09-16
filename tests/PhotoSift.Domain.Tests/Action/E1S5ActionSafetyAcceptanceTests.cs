using System.Collections.Immutable;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Domain.Tests.Action;

public sealed class E1S5ActionSafetyAcceptanceTests
{
    [Fact]
    public void ExternalLinkThatDisappearedSinceReviewBlocksTheFinalRemoval()
    {
        var approved = ActionGroups.OneKey(paths: [@"C:\scope\a.jpg"], linkCount: 2);
        var current = ImmutableArray.Create(ActionGroups.Refresh(approved.Members[0], linkCount: 1));
        var input = ActionGroups.Input(
            approved,
            current,
            remainingApproved: [1],
            overrideApproved: [],
            overrideExact: false);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, result.Reason);
    }

    [Fact]
    public void ConfirmedSamePhysicalRecycleBinLinkRemainsAnExternalSurvivor()
    {
        var approved = ActionGroups.OneKey(
            [@"C:\scope\already-recycled.jpg", @"C:\scope\remaining.jpg"],
            linkCount: 2);
        var current = ImmutableArray.Create(ActionGroups.Refresh(approved.Members[1], linkCount: 2));
        var input = ActionGroups.Input(approved, current, remainingApproved: [2]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.Allowed, result.Kind);
        Assert.Equal(SurvivorDecisionKind.Allowed, result.Survivor.Kind);
        var survivor = Assert.Single(result.Survivor.ExternalSurvivors);
        Assert.Equal((uint)2, survivor.LinkCount);
        Assert.Equal(1, survivor.KnownPathCount);
    }

    [Fact]
    public void ExactOverrideDoesNotAuthorizeAnInconsistentCurrentProjection()
    {
        var key = ActionGroups.Key(1);
        var approved = ActionGroups.Group(
            ActionGroups.Occurrence(1, key, @"C:\scope\a.jpg", linkCount: 2),
            ActionGroups.Occurrence(2, key, @"C:\scope\b.jpg", linkCount: 1));
        var input = ActionGroups.Input(
            approved,
            approved.Members,
            remainingApproved: [1, 2],
            overrideApproved: [1, 2],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }
}
