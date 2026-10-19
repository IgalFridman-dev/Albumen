using Albumen.Application.Action;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Windows.Action;
using Xunit;

namespace Albumen.Infrastructure.Tests.ActionExecution;

public sealed class RecycleBinClassificationTests
{
    internal static RecycleBinEvidence Success() => new(1, 0, true, 0, false, 1, 0, 0, true, false, false);

    [Theory]
    [InlineData("performNotEntered", false)]
    [InlineData("performNotEntered", true)]
    [InlineData("permissionWithoutHandoff", false)]
    [InlineData("permissionWithoutHandoff", true)]
    [InlineData("finalReleaseWithoutHandoff", false)]
    [InlineData("finalReleaseWithoutHandoff", true)]
    public void ExplicitImpossibleNewObservationsCannotBecomeSuccess(string impossible, bool unchanged)
    {
        var evidence = Success() with
        { NamespaceHandoffOccurred = true, PerformOperationsEntered = true, SelectedHandleReleased = true, OriginalObjectProvenUnchanged = unchanged };
        evidence = impossible switch
        {
            "performNotEntered" => evidence with { PerformOperationsEntered = false },
            "permissionWithoutHandoff" => evidence with { NamespaceHandoffOccurred = false },
            _ => evidence with { NamespaceHandoffOccurred = false, PermissionGranted = false },
        };
        var result = RecycleBinClassifier.Classify(evidence, ActionSafetyReason.None);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(ActionSafetyReason.CallbackContradiction, result.Reason);
    }

    [Fact]
    public void UnknownHandoffAfterPerformEntryCannotUseNoPermissionShortcut()
    {
        var evidence = new RecycleBinEvidence(0, 0, false, null, null, 0, 0, null, false, false, false)
        { NamespaceHandoffOccurred = null, PerformOperationsEntered = true };
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, RecycleBinClassifier.Classify(evidence, ActionSafetyReason.Interrupted).Outcome);
    }

    [Theory]
    [InlineData(false, false, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData(null, false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData(true, false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData(true, true, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData(null, true, RecycleBinOutcomeKind.NotRemoved)]
    public void NoPermissionNeedsKnownNoHandoffOrAffirmativeOriginalProof(bool? handoff, bool unchanged, RecycleBinOutcomeKind expected)
    {
        var evidence = new RecycleBinEvidence(0, 0, false, null, null, 0, 0, null, false, false, unchanged)
        { NamespaceHandoffOccurred = handoff, PerformOperationsEntered = false };
        Assert.Equal(expected, RecycleBinClassifier.Classify(evidence, ActionSafetyReason.Changed).Outcome);
    }

    [Theory]
    [InlineData("unexpectedPre")]
    [InlineData("unexpectedPost")]
    [InlineData("exception")]
    [InlineData("interrupted")]
    public void HandoffContradictionOrInterruptionOverridesUnchangedProofWithoutPermission(string signal)
    {
        var evidence = new RecycleBinEvidence(0, 0, false, null, null, 0, 0, null, false, false, true)
        { NamespaceHandoffOccurred = true, PerformOperationsEntered = true };
        evidence = signal switch
        {
            "unexpectedPre" => evidence with { UnexpectedPreDeleteCount = 1 },
            "unexpectedPost" => evidence with { UnexpectedPostDeleteCount = 1 },
            "exception" => evidence with { CallbackException = true },
            _ => evidence with { RunnerInterrupted = true },
        };
        var result = RecycleBinClassifier.Classify(evidence, ActionSafetyReason.Interrupted);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(signal == "interrupted" ? ActionSafetyReason.Interrupted : ActionSafetyReason.CallbackContradiction, result.Reason);
    }

    [Fact]
    public void InterruptedRunnerAfterPermissionCannotBecomeSuccessOrAffirmativeNotRemoved()
    {
        var evidence = Success() with { RunnerInterrupted = true, OriginalObjectProvenUnchanged = true };
        var result = RecycleBinClassifier.Classify(evidence, ActionSafetyReason.Interrupted);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(ActionSafetyReason.Interrupted, result.Reason);
        Assert.False(result.Evidence.CallbackException);
    }

    [Fact]
    public void OnlyCompleteNoncontradictorySuccessIsRecycled()
    {
        var evidence = Success();
        var result = RecycleBinClassifier.Classify(evidence, ActionSafetyReason.None);
        Assert.Equal(RecycleBinOutcomeKind.Recycled, result.Outcome);
        Assert.Same(evidence, result.Evidence);
    }

    [Theory]
    [InlineData("duplicatePre")]
    [InlineData("unexpectedPre")]
    [InlineData("duplicatePost")]
    [InlineData("unexpectedPost")]
    [InlineData("exception")]
    [InlineData("postWithoutPre")]
    public void ContradictionOverridesCompleteSuccessAndUnchangedProof(string contradiction)
    {
        var evidence = contradiction switch
        {
            "duplicatePre" => Success() with { ValidPreDeleteCount = 2 },
            "unexpectedPre" => Success() with { UnexpectedPreDeleteCount = 1 },
            "duplicatePost" => Success() with { MatchingPostDeleteCount = 2 },
            "unexpectedPost" => Success() with { UnexpectedPostDeleteCount = 1 },
            "exception" => Success() with { CallbackException = true },
            _ => Success() with { ValidPreDeleteCount = 0 },
        };
        evidence = evidence with { OriginalObjectProvenUnchanged = true };
        var result = RecycleBinClassifier.Classify(evidence, ActionSafetyReason.None);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(ActionSafetyReason.CallbackContradiction, result.Reason);
    }

    [Theory]
    [InlineData("noPost", false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("noPost", true, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData("deleteFailed", false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("deleteFailed", true, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData("noNewItem", false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("noNewItem", true, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData("performFailed", false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("performFailed", true, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData("aborted", false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("aborted", true, RecycleBinOutcomeKind.NotRemoved)]
    [InlineData("abortUnknown", false, RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("abortUnknown", true, RecycleBinOutcomeKind.NotRemoved)]
    public void IncompleteResultRequiresAffirmativeProtectedOriginalProof(string missing, bool unchanged, RecycleBinOutcomeKind expected)
    {
        // Failed platform evidence does not claim a successful post callback.
        var evidence = new RecycleBinEvidence(1, 0, true, 0, false, 1, 0, -1, false, false, unchanged);
        evidence = missing switch
        {
            "noPost" => evidence with { MatchingPostDeleteCount = 0, DeleteHResult = null },
            "deleteFailed" => evidence,
            "noNewItem" => evidence with { DeleteHResult = 0 },
            "performFailed" => evidence with { PerformOperationsHResult = -1 },
            "aborted" => evidence with { OperationsAborted = true },
            _ => evidence with { OperationsAborted = null },
        };
        Assert.Equal(expected, RecycleBinClassifier.Classify(evidence, ActionSafetyReason.CannotRecycle).Outcome);
    }

    [Fact]
    public void SuccessfulPostAndFailedPlatformResultIsContradictoryEvenIfOriginalObserved()
    {
        var evidence = Success() with { PerformOperationsHResult = -1, OriginalObjectProvenUnchanged = true };
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation,
            RecycleBinClassifier.Classify(evidence, ActionSafetyReason.CannotRecycle).Outcome);
    }

    [Theory]
    [InlineData(ActionSafetyReason.Interrupted)]
    [InlineData(ActionSafetyReason.Changed)]
    [InlineData(ActionSafetyReason.Unavailable)]
    public void FailureBeforePermissionIsNotRemoved(ActionSafetyReason reason)
    {
        var evidence = new RecycleBinEvidence(0, 0, false, null, null, 0, 0, null, false, false, false)
        { NamespaceHandoffOccurred = false, PerformOperationsEntered = false };
        var result = RecycleBinClassifier.Classify(evidence, reason);
        Assert.Equal(RecycleBinOutcomeKind.NotRemoved, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }
}
