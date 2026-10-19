using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Scan;

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
    [MemberData(nameof(EvidenceCorrectionEdges))]
    public void ExactTerminalCorrectionAllowsOnlySucceededToChanged(
        StageOutcome current,
        StageOutcome next,
        bool expected)
    {
        Assert.Equal(expected, ScanTransitionPolicy.CanTransition(ScanStage.Exact, current, next));
    }

    public static TheoryData<StageOutcome, StageOutcome, bool> EvidenceCorrectionEdges() => new()
    {
        {
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed),
            true
        },
        {
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            false
        },
        {
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault),
            false
        },
        {
            new StageOutcome(ScanStageState.Unsupported, ScanReason.UnsupportedExtension),
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed),
            false
        },
        {
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed),
            new StageOutcome(ScanStageState.Running, ScanReason.None),
            false
        },
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
        var rows = new[]
        {
            Checkpoint(ScanStage.Enumeration, succeeded, succeeded),
            Checkpoint(ScanStage.Exact, succeeded, succeeded),
        };

        Assert.True(ScanTransitionPolicy.CanComplete(
            succeeded, rows, accounting));
        Assert.False(ScanTransitionPolicy.CanComplete(
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            rows, accounting));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded,
            [rows[0], Checkpoint(ScanStage.Exact,
                new StageOutcome(ScanStageState.Running, ScanReason.None), succeeded)],
            new ScanAccounting(1, 0, 0, 0, 0, 1)));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded, rows, new ScanAccounting(1, 0, 0, 0, 0, 1)));
    }

    [Fact]
    public void CompleteRejectsMissingDuplicateMixedScanAndContradictorySiblingRows()
    {
        var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var failed = new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure);
        var analyzed = new ScanAccounting(1, 1, 0, 0, 0, 0);
        var enumeration = Checkpoint(ScanStage.Enumeration, succeeded, succeeded);
        var exact = Checkpoint(ScanStage.Exact, succeeded, succeeded);

        Assert.False(ScanTransitionPolicy.CanComplete(succeeded, [], analyzed));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded, [], new ScanAccounting(0, 0, 0, 0, 0, 0)));
        Assert.False(ScanTransitionPolicy.CanComplete(succeeded, [enumeration], analyzed));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded, [enumeration, exact, exact], analyzed));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded,
            [enumeration, Checkpoint(
                ScanStage.Exact,
                succeeded,
                succeeded,
                scanId: Guid.Parse("e7d85894-b30d-4bc2-8b56-525c08cff4e2"))],
            analyzed));
        Assert.False(ScanTransitionPolicy.CanComplete(
            succeeded,
            [enumeration, Checkpoint(ScanStage.Exact,
                new StageOutcome(ScanStageState.NotApplicable, ScanReason.ReadFailure), failed)],
            analyzed));
    }

    [Fact]
    public void AccountingIsRecomputedFromOneStagePairPerOccurrence()
    {
        var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var unsupported = new StageOutcome(ScanStageState.Unsupported, ScanReason.UnsupportedExtension);
        var canceled = new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled);
        var rows = new[]
        {
            Checkpoint(ScanStage.Enumeration, succeeded, succeeded, occurrenceId: 7),
            Checkpoint(ScanStage.Exact, succeeded, succeeded, occurrenceId: 7),
            Checkpoint(ScanStage.Enumeration, succeeded, succeeded, occurrenceId: 8),
            Checkpoint(ScanStage.Exact, unsupported, succeeded, occurrenceId: 8),
            Checkpoint(ScanStage.Enumeration, succeeded, succeeded, occurrenceId: 9),
            Checkpoint(ScanStage.Exact, canceled, succeeded, occurrenceId: 9),
            Checkpoint(ScanStage.Enumeration, succeeded, succeeded, occurrenceId: 10),
            Checkpoint(ScanStage.Exact,
                new StageOutcome(ScanStageState.Running, ScanReason.None), succeeded, occurrenceId: 10),
        };

        Assert.True(ScanTransitionPolicy.TryRecomputeAccounting(rows, out var accounting));
        Assert.Equal(new ScanAccounting(4, 1, 1, 0, 1, 1), accounting);
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
        StageOutcome prerequisiteOutcome,
        long occurrenceId = 7,
        Guid? scanId = null) => new(
            scanId ?? Guid.Parse("5a7bce28-1eab-4900-b22c-a02cc6900e68"),
            occurrenceId,
            stage,
            3,
            Guid.Parse("a25e6cb7-dd4f-4825-ab8a-86f148797f4e"),
            outcome,
            stage == ScanStage.Exact
                ? new ScanStagePrerequisite(ScanStage.Enumeration, prerequisiteOutcome)
                : null);
}
