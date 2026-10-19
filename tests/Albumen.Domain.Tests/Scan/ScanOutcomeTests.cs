using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Scan;

public sealed class ScanOutcomeTests
{
    [Theory]
    [InlineData(ScanStageState.Pending)]
    [InlineData(ScanStageState.Running)]
    [InlineData(ScanStageState.Succeeded)]
    public void ActiveAndSuccessfulOutcomesRequireNoReason(ScanStageState state)
    {
        Assert.Equal(new StageOutcome(state, ScanReason.None), new StageOutcome(state, ScanReason.None));
        Assert.Throws<ArgumentException>(() => new StageOutcome(state, ScanReason.Changed));
    }

    [Theory]
    [InlineData(ScanStageState.Unsupported, ScanReason.UnsupportedExtension)]
    [InlineData(ScanStageState.Unsupported, ScanReason.CodecUnavailable)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.AccessDenied)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.SharingViolation)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.Missing)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.Changed)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.ReadFailure)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.UnknownSafety)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.IncompleteEnumeration)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault)]
    [InlineData(ScanStageState.Canceled, ScanReason.Canceled)]
    public void TerminalOutcomesAcceptTheirTypedReasons(ScanStageState state, ScanReason reason)
    {
        var outcome = new StageOutcome(state, reason);

        Assert.Equal(state, outcome.State);
        Assert.Equal(reason, outcome.Reason);
    }

    [Theory]
    [InlineData(ScanReason.UnsupportedExtension)]
    [InlineData(ScanReason.AccessDenied)]
    [InlineData(ScanReason.UnsafeLocation)]
    [InlineData(ScanReason.CapabilityUnavailable)]
    public void NotApplicableRetainsAnyDefinedPrerequisiteReason(ScanReason reason)
    {
        var outcome = new StageOutcome(ScanStageState.NotApplicable, reason);

        Assert.Equal(reason, outcome.Reason);
    }

    [Theory]
    [InlineData(ScanStageState.Unsupported, ScanReason.None)]
    [InlineData(ScanStageState.Unsupported, ScanReason.ReadFailure)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.None)]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.ProgrammingFault)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.None)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.SharingViolation)]
    [InlineData(ScanStageState.Canceled, ScanReason.None)]
    [InlineData(ScanStageState.Canceled, ScanReason.Changed)]
    [InlineData(ScanStageState.NotApplicable, ScanReason.None)]
    public void OutcomesRejectIllegalStateReasonCombinations(ScanStageState state, ScanReason reason)
    {
        Assert.Throws<ArgumentException>(() => new StageOutcome(state, reason));
    }

    [Fact]
    public void OutcomesRejectUndefinedStateOrReasonValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StageOutcome((ScanStageState)999, ScanReason.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StageOutcome(ScanStageState.Succeeded, (ScanReason)999));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StageOutcome(ScanStageState.NotApplicable, (ScanReason)999));
    }
}
