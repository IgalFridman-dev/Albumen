using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Xunit;

namespace Albumen.Domain.Tests.Action;

public sealed class E1S5FinalSafetyAcceptanceTests
{
    [Fact]
    public void RecycledOriginalDoesNotSpendItsPhysicalLinkAndFreshLossRequiresExactOverrideCoverage()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\first.jpg", @"C:\scope\second.jpg"], 3);
        var before = ActionGroups.Input(approved, approved.Members, remainingApproved: [1, 2]);
        Assert.Equal(ActionSafetyDecisionKind.Allowed, ActionSafetyPolicy.Evaluate(before).Kind);

        // A confirmed recycled occurrence leaves the current original-path set.
        // Its physical link is neither subtracted nor assumed to remain forever.
        var remaining = ActionGroups.Refresh(approved.Members[1], linkCount: 3);
        var afterRecycle = ActionGroups.Input(approved, [remaining], remainingApproved: [2]);
        var retainedLink = ActionSafetyPolicy.Evaluate(afterRecycle);
        Assert.Equal(ActionSafetyDecisionKind.Allowed, retainedLink.Kind);
        var external = Assert.Single(retainedLink.Survivor.ExternalSurvivors);
        Assert.Equal((uint)3, external.LinkCount);
        Assert.Equal(1, external.KnownPathCount);

        var afterLoss = afterRecycle with { CurrentOccurrences = [ActionGroups.Refresh(remaining, linkCount: 1)] };
        Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, ActionSafetyPolicy.Evaluate(afterLoss).Reason);
        Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, ActionSafetyPolicy.Evaluate(afterLoss with
            { OverrideEvidenceExact = true, OverrideApprovedOccurrenceIds = [1] }).Reason);
        Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, ActionSafetyPolicy.Evaluate(afterLoss with
            { OverrideEvidenceExact = false, OverrideApprovedOccurrenceIds = [1, 2] }).Reason);
        var covered = ActionSafetyPolicy.Evaluate(afterLoss with
            { OverrideEvidenceExact = true, OverrideApprovedOccurrenceIds = [1, 2] });
        Assert.Equal(ActionSafetyDecisionKind.Allowed, covered.Kind);
        Assert.Equal(SurvivorDecisionKind.OverrideRequired, covered.Survivor.Kind);
        Assert.Equal((uint)3, approved.Members[1].Allocation.LinkCount);
    }

    [Theory]
    [InlineData("unknown", ActionSafetyReason.DependentOutcomeUnknown)]
    [InlineData("missing", ActionSafetyReason.Missing)]
    [InlineData("changed-path", ActionSafetyReason.Changed)]
    [InlineData("unstable-links", ActionSafetyReason.InconsistentLinks)]
    public void CompleteOverrideCoverageCannotTurnFreshUnsafeDependentFactsIntoPermission(string change,
        ActionSafetyReason reason)
    {
        var approved = ActionGroups.OneKey([@"C:\scope\first.jpg", @"C:\scope\last.jpg"], 2);
        var remaining = ActionGroups.Refresh(approved.Members[1], linkCount: 1);
        var input = ActionGroups.Input(approved, [remaining], [2], [1, 2], overrideExact: true);
        Assert.Equal(ActionSafetyDecisionKind.Allowed, ActionSafetyPolicy.Evaluate(input).Kind);
        input = change switch
        {
            "unknown" => input with { HasDependentUnknown = true },
            "missing" => input with { CurrentOccurrences = [] },
            "changed-path" => input with { CurrentOccurrences = [ActionGroups.Refresh(remaining, path: @"C:\scope\replacement.jpg")] },
            _ => input with { CurrentOccurrences = [ActionGroups.Refresh(remaining, stable: false)] },
        };
        var result = ActionSafetyPolicy.Evaluate(input);
        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(reason, result.Reason);
    }
}
