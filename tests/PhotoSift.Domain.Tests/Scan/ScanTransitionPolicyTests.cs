using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Scan;

public sealed class ScanTransitionPolicyTests
{
    [Theory]
    [MemberData(nameof(LifecycleEdges))]
    public void LifecyclePolicyAcceptsOnlyTheDeclaredEdges(
        ScanLifecycleState current,
        ScanLifecycleState next,
        bool expected)
    {
        Assert.Equal(expected, ScanTransitionPolicy.CanTransition(current, next));
    }

    public static TheoryData<ScanLifecycleState, ScanLifecycleState, bool> LifecycleEdges() => new()
    {
        { ScanLifecycleState.Preflight, ScanLifecycleState.Enumerating, true },
        { ScanLifecycleState.Preflight, ScanLifecycleState.Incomplete, true },
        { ScanLifecycleState.Preflight, ScanLifecycleState.Faulted, true },
        { ScanLifecycleState.Enumerating, ScanLifecycleState.Exact, true },
        { ScanLifecycleState.Enumerating, ScanLifecycleState.Pausing, true },
        { ScanLifecycleState.Enumerating, ScanLifecycleState.Incomplete, true },
        { ScanLifecycleState.Enumerating, ScanLifecycleState.Faulted, true },
        { ScanLifecycleState.Exact, ScanLifecycleState.Complete, true },
        { ScanLifecycleState.Exact, ScanLifecycleState.Pausing, true },
        { ScanLifecycleState.Exact, ScanLifecycleState.Incomplete, true },
        { ScanLifecycleState.Exact, ScanLifecycleState.Faulted, true },
        { ScanLifecycleState.Pausing, ScanLifecycleState.Paused, true },
        { ScanLifecycleState.Pausing, ScanLifecycleState.Incomplete, true },
        { ScanLifecycleState.Pausing, ScanLifecycleState.Faulted, true },
        { ScanLifecycleState.Paused, ScanLifecycleState.Enumerating, true },
        { ScanLifecycleState.Paused, ScanLifecycleState.Exact, true },
        { ScanLifecycleState.Paused, ScanLifecycleState.Incomplete, true },
        { ScanLifecycleState.Paused, ScanLifecycleState.Faulted, true },
        { ScanLifecycleState.Preflight, ScanLifecycleState.Complete, false },
        { ScanLifecycleState.Enumerating, ScanLifecycleState.Complete, false },
        { ScanLifecycleState.Exact, ScanLifecycleState.Enumerating, false },
        { ScanLifecycleState.Pausing, ScanLifecycleState.Complete, false },
        { ScanLifecycleState.Paused, ScanLifecycleState.Complete, false },
        { ScanLifecycleState.Complete, ScanLifecycleState.Exact, false },
        { ScanLifecycleState.Incomplete, ScanLifecycleState.Enumerating, false },
        { ScanLifecycleState.Faulted, ScanLifecycleState.Preflight, false },
    };

    [Theory]
    [MemberData(nameof(StageEdges))]
    public void StagePolicyAcceptsOnlyRunningExactToTerminalEdges(
        ScanStage stage,
        ScanStageState current,
        ScanStageState next,
        bool expected)
    {
        Assert.Equal(expected, ScanTransitionPolicy.CanTransition(stage, current, next));
    }

    public static TheoryData<ScanStage, ScanStageState, ScanStageState, bool> StageEdges() => new()
    {
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.Succeeded, true },
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.Unsupported, true },
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.FailedRetryable, true },
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.FailedTerminal, true },
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.Canceled, true },
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.Running, false },
        { ScanStage.Exact, ScanStageState.Running, ScanStageState.NotApplicable, false },
        { ScanStage.Exact, ScanStageState.Succeeded, ScanStageState.Running, false },
        { ScanStage.Exact, ScanStageState.FailedRetryable, ScanStageState.Running, false },
        { ScanStage.Enumeration, ScanStageState.Running, ScanStageState.Succeeded, false },
        { ScanStage.Enumeration, ScanStageState.Succeeded, ScanStageState.FailedRetryable, false },
    };

    [Theory]
    [InlineData(ScanStageState.Pending, false)]
    [InlineData(ScanStageState.Running, false)]
    [InlineData(ScanStageState.Succeeded, true)]
    [InlineData(ScanStageState.Unsupported, true)]
    [InlineData(ScanStageState.FailedRetryable, true)]
    [InlineData(ScanStageState.FailedTerminal, true)]
    [InlineData(ScanStageState.Canceled, true)]
    [InlineData(ScanStageState.NotApplicable, true)]
    public void StageTerminalPredicateRejectsUnfinishedRows(ScanStageState state, bool expected)
    {
        Assert.Equal(expected, ScanTransitionPolicy.IsTerminal(state));
    }

    [Theory]
    [InlineData(ScanLifecycleState.Preflight, false)]
    [InlineData(ScanLifecycleState.Enumerating, false)]
    [InlineData(ScanLifecycleState.Exact, false)]
    [InlineData(ScanLifecycleState.Complete, true)]
    [InlineData(ScanLifecycleState.Pausing, false)]
    [InlineData(ScanLifecycleState.Paused, false)]
    [InlineData(ScanLifecycleState.Incomplete, true)]
    [InlineData(ScanLifecycleState.Faulted, true)]
    public void LifecycleTerminalPredicateRejectsActiveAndPausedRows(
        ScanLifecycleState state,
        bool expected)
    {
        Assert.Equal(expected, ScanTransitionPolicy.IsTerminal(state));
    }

    [Fact]
    public void CompleteRequiresSuccessfulEnumerationTerminalRowsAndMatchingAccounting()
    {
        var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var accounting = new ScanAccounting(1, 1, 0, 0, 0, 0);
        var exact = Checkpoint(ScanStage.Exact, succeeded, succeeded);

        Assert.True(ScanTransitionPolicy.CanComplete(
            succeeded, [exact], accounting, accounting));
        Assert.False(ScanTransitionPolicy.CanComplete(
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            [exact], accounting, accounting));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded,
            [Checkpoint(ScanStage.Exact,
                new StageOutcome(ScanStageState.Running, ScanReason.None), succeeded)],
            new ScanAccounting(1, 0, 0, 0, 0, 1),
            new ScanAccounting(1, 0, 0, 0, 0, 1)));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded, [exact], accounting, new ScanAccounting(1, 0, 0, 0, 0, 1)));
    }

    [Fact]
    public void UndefinedValuesAreRejectedByPurePredicates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScanTransitionPolicy.CanTransition((ScanLifecycleState)999, ScanLifecycleState.Faulted));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScanTransitionPolicy.CanTransition(ScanStage.Exact, (ScanStageState)999, ScanStageState.Succeeded));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScanTransitionPolicy.IsTerminal((ScanStageState)999));
    }

    private static ScanCheckpoint Checkpoint(
        ScanStage stage,
        StageOutcome outcome,
        StageOutcome prerequisiteOutcome) => new(
            Guid.Parse("5a7bce28-1eab-4900-b22c-a02cc6900e68"),
            7,
            stage,
            3,
            Guid.Parse("a25e6cb7-dd4f-4825-ab8a-86f148797f4e"),
            outcome,
            stage == ScanStage.Exact
                ? new ScanStagePrerequisite(ScanStage.Enumeration, prerequisiteOutcome)
                : null,
            false);
}
