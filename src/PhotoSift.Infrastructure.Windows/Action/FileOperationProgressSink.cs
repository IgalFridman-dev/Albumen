using System.Runtime.InteropServices.Marshalling;
using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;

namespace PhotoSift.Infrastructure.Windows.Action;

[GeneratedComClass]
internal sealed partial class FileOperationProgressSink(
    Func<nint, bool> isExpectedItem,
    Func<ActionSafetyReason> validateAndRelease,
    CancellationToken cancellationToken,
    IFileOperationCallbackObserver? callbackObserver = null) : IFileOperationProgressSink
{
    private const int Denied = unchecked((int)0x80004004); // E_ABORT
    private int preAttempts;
    private int starts;
    private int finishes;
    private RecycleBinEvidence evidence = new(0, 0, false, null, null, 0, 0, null, false, false, false)
    {
        SelectedHandleReleased = false,
        RunnerInterrupted = false,
    };
    internal RecycleBinEvidence Evidence => evidence;
    internal ActionSafetyReason Reason { get; private set; } = ActionSafetyReason.None;

    public int PreDeleteItem(uint flags, nint item)
    {
        callbackObserver?.Observe(FileOperationCallbackBoundary.PreDeleteItem);
        try
        {
            preAttempts++;
            if (preAttempts != 1 || evidence.UnexpectedPreDeleteCount != 0 || evidence.UnexpectedPostDeleteCount != 0
                || evidence.CallbackException || finishes != 0 || item == 0 || !isExpectedItem(item))
                return UnexpectedPre();
            if (cancellationToken.IsCancellationRequested)
            {
                Reason = ActionSafetyReason.Interrupted;
                evidence = evidence with { ValidationReason = Reason };
                return Denied;
            }
            var reason = validateAndRelease();
            Reason = reason;
            evidence = evidence with { ValidationReason = reason };
            if (reason != ActionSafetyReason.None) return Denied;
            // The selected handle was released synchronously as the last lease
            // action. Nothing awaits or dispatches between that release and S_OK.
            evidence = evidence with { ValidPreDeleteCount = 1, SelectedHandleReleased = true, PermissionGranted = true };
            return 0;
        }
        catch (Exception)
        {
            evidence = evidence with { CallbackException = true };
            Reason = ActionSafetyReason.CallbackContradiction;
            return Denied;
        }
    }

    public int PostDeleteItem(uint flags, nint item, int result, nint newlyCreated)
    {
        try
        {
            if (!evidence.PermissionGranted || item == 0 || !isExpectedItem(item)
                || evidence.MatchingPostDeleteCount != 0 || finishes != 0)
                return UnexpectedPost();
            evidence = evidence with
            {
                MatchingPostDeleteCount = 1,
                DeleteHResult = result,
                NewlyCreatedItemPresent = newlyCreated != 0,
            };
            return 0;
        }
        catch (Exception)
        {
            evidence = evidence with { CallbackException = true };
            Reason = ActionSafetyReason.CallbackContradiction;
            return Denied;
        }
    }

    public int StartOperations()
    {
        starts++;
        return starts != 1 || preAttempts != 0 || finishes != 0 ? UnexpectedPre() : 0;
    }
    public int FinishOperations(int result)
    {
        finishes++;
        if (finishes != 1) return UnexpectedPost();
        evidence = evidence with { FinishOperationsHResult = result };
        return 0;
    }

    // No operation other than the sole approved delete can receive permission.
    public int PreRenameItem(uint flags, nint item, nint newName) => UnexpectedPre();
    public int PostRenameItem(uint flags, nint item, nint newName, int result, nint newlyCreated) => UnexpectedPost();
    public int PreMoveItem(uint flags, nint item, nint destination, nint newName) => UnexpectedPre();
    public int PostMoveItem(uint flags, nint item, nint destination, nint newName, int result, nint newlyCreated) => UnexpectedPost();
    public int PreCopyItem(uint flags, nint item, nint destination, nint newName) => UnexpectedPre();
    public int PostCopyItem(uint flags, nint item, nint destination, nint newName, int result, nint newlyCreated) => UnexpectedPost();
    public int PreNewItem(uint flags, nint destination, nint newName) => UnexpectedPre();
    public int PostNewItem(uint flags, nint destination, nint newName, nint template, uint attributes, int result, nint newlyCreated) => UnexpectedPost();
    public int UpdateProgress(uint total, uint complete) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;

    private int UnexpectedPre()
    {
        evidence = evidence with { UnexpectedPreDeleteCount = evidence.UnexpectedPreDeleteCount + 1 };
        Reason = ActionSafetyReason.CallbackContradiction;
        return Denied;
    }
    private int UnexpectedPost()
    {
        evidence = evidence with { UnexpectedPostDeleteCount = evidence.UnexpectedPostDeleteCount + 1 };
        Reason = ActionSafetyReason.CallbackContradiction;
        return Denied;
    }
}

internal enum FileOperationCallbackBoundary { PreDeleteItem }
internal interface IFileOperationCallbackObserver
{
    void Observe(FileOperationCallbackBoundary boundary);
}

internal static class RecycleBinClassifier
{
    internal static RecycleBinResult Classify(RecycleBinEvidence evidence, ActionSafetyReason reason)
    {
        var namespaceUncertain = evidence.NamespaceHandoffOccurred != false || evidence.PermissionGranted;
        if (ExplicitPhaseContradiction(evidence) || namespaceUncertain && Contradictory(evidence))
            return new(RecycleBinOutcomeKind.UnknownPendingRevalidation, ActionSafetyReason.CallbackContradiction, evidence);
        if (namespaceUncertain && evidence.RunnerInterrupted == true)
            return new(RecycleBinOutcomeKind.UnknownPendingRevalidation, ActionSafetyReason.Interrupted, evidence);
        if (evidence.PermissionGranted && evidence.ValidPreDeleteCount == 1
            && evidence.PerformOperationsHResult >= 0 && evidence.OperationsAborted == false
            && evidence.MatchingPostDeleteCount == 1 && evidence.DeleteHResult >= 0 && evidence.NewlyCreatedItemPresent)
            return new(RecycleBinOutcomeKind.Recycled, ActionSafetyReason.None, evidence);
        if (!evidence.PermissionGranted && evidence.NamespaceHandoffOccurred == false || evidence.OriginalObjectProvenUnchanged)
            return new(RecycleBinOutcomeKind.NotRemoved, reason == ActionSafetyReason.None ? ActionSafetyReason.CannotRecycle : reason, evidence);
        return new(RecycleBinOutcomeKind.UnknownPendingRevalidation,
            reason == ActionSafetyReason.None ? ActionSafetyReason.CannotRecycle : reason, evidence);
    }

    private static bool ExplicitPhaseContradiction(RecycleBinEvidence value) =>
        value.NamespaceHandoffOccurred == false && (value.PermissionGranted || value.SelectedHandleReleased == true)
        || !value.PermissionGranted && value.SelectedHandleReleased == true
        || value.PerformOperationsEntered == false && (value.PermissionGranted || value.ValidPreDeleteCount != 0
            || value.MatchingPostDeleteCount != 0 || value.PerformOperationsHResult is not null
            || value.OperationsAborted is not null || value.GetAbortedHResult is not null
            || value.DeleteHResult is not null || value.NewlyCreatedItemPresent || value.FinishOperationsHResult is not null);

    private static bool Contradictory(RecycleBinEvidence value)
    {
        var successfulPost = value.MatchingPostDeleteCount == 1 && value.DeleteHResult >= 0 && value.NewlyCreatedItemPresent;
        return value.UnexpectedPreDeleteCount != 0 || value.UnexpectedPostDeleteCount != 0 || value.CallbackException
            || value.ValidPreDeleteCount != (value.PermissionGranted ? 1 : 0) || value.MatchingPostDeleteCount is < 0 or > 1
            || !value.PermissionGranted && value.MatchingPostDeleteCount != 0
            || value.PermissionGranted && (value.SelectedHandleReleased == false
                || value.ValidationReason is not null and not ActionSafetyReason.None || value.PreparationHResult < 0)
            || successfulPost && (value.PerformOperationsHResult < 0 || value.OperationsAborted == true
                || value.GetAbortedHResult < 0 || value.FinishOperationsHResult < 0)
            || value.MatchingPostDeleteCount == 0 && (value.DeleteHResult is not null || value.NewlyCreatedItemPresent)
            || value.DeleteHResult < 0 && value.NewlyCreatedItemPresent;
    }
}
