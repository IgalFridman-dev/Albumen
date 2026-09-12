using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;

namespace PhotoSift.Infrastructure.Windows.Action;

internal interface IRecycleBinLease : IDisposable
{
    bool NamespaceHandoffOccurred { get; }
    ActionSafetyReason PrepareForPerformOperations();
    ActionSafetyReason ValidateAndReleaseForPreDelete();
    bool ProveOriginalUnchanged();
}

internal static class StaFileOperationRunner
{
    internal static Task<RecycleBinResult> RunAsync(string originalPath, Func<IFileOperationDriver> createDriver,
        Func<IRecycleBinLease> openLease, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RecycleBinResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => completion.SetResult(Run(originalPath, createDriver, openLease, cancellationToken)))
        {
            IsBackground = true,
            Name = "PhotoSift one-item Recycle Bin STA",
        };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception exception)
        {
            var evidence = EmptyEvidence() with { PreparationHResult = exception.HResult, RunnerInterrupted = true };
            completion.TrySetResult(RecycleBinClassifier.Classify(evidence, ActionSafetyReason.Interrupted));
        }
        // Deliberately not cancellation-bound. After handoff or permission all
        // observations, terminal delivery and cleanup settle even if canceled.
        return completion.Task;
    }

    private static RecycleBinResult Run(string originalPath, Func<IFileOperationDriver> createDriver,
        Func<IRecycleBinLease> openLease, CancellationToken cancellationToken)
    {
        IFileOperationDriver? driver = null;
        IRecycleBinLease? lease = null;
        FileOperationProgressSink? sink = null;
        var evidence = EmptyEvidence();
        var reason = ActionSafetyReason.None;
        var initialized = false;
        var advised = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            driver = createDriver();
            Prepare(driver.InitializeCom());
            initialized = true;
            lease = openLease();
            cancellationToken.ThrowIfCancellationRequested();
            Prepare(driver.CreateOperation());
            Prepare(driver.CreateItem(originalPath));
            sink = new(driver.IsExpectedItem, lease.ValidateAndReleaseForPreDelete, cancellationToken);
            Prepare(driver.Advise(sink));
            advised = true;
            Prepare(driver.SetOperationFlags(NativeFileOperationDriver.RecycleOnlyFlags));
            Prepare(driver.DeleteItem());
            cancellationToken.ThrowIfCancellationRequested();
            var handoffReason = lease.PrepareForPerformOperations();
            evidence = evidence with { NamespaceHandoffOccurred = lease.NamespaceHandoffOccurred };
            if (handoffReason != ActionSafetyReason.None) throw new WindowsActionLeaseException(handoffReason);
            cancellationToken.ThrowIfCancellationRequested();
            // This records entry into the driver call, never an inferred HRESULT.
            evidence = evidence with { PerformOperationsEntered = true };
            var performResult = driver.PerformOperations();
            CaptureSink();
            evidence = evidence with { PerformOperationsHResult = performResult };
            reason = sink.Reason;
            if (performResult < 0 && reason == ActionSafetyReason.None) reason = ActionSafetyReason.CannotRecycle;
            var abortedResult = driver.GetAnyOperationsAborted(out var aborted);
            evidence = evidence with { GetAbortedHResult = abortedResult, OperationsAborted = abortedResult >= 0 ? aborted : null };
            if ((abortedResult < 0 || aborted) && reason == ActionSafetyReason.None) reason = ActionSafetyReason.CannotRecycle;
        }
        catch (PreparationException exception)
        {
            reason = ActionSafetyReason.CannotRecycle;
            CaptureSink();
            evidence = evidence with { PreparationHResult = exception.HResult };
        }
        catch (Exception exception)
        {
            evidence = evidence with { NamespaceHandoffOccurred = lease?.NamespaceHandoffOccurred ?? evidence.NamespaceHandoffOccurred };
            CaptureSink();
            evidence = evidence with
            {
                PreparationHResult = evidence.PermissionGranted || evidence.NamespaceHandoffOccurred == true ? evidence.PreparationHResult : exception.HResult,
                RunnerInterrupted = evidence.PermissionGranted
                    || exception is not WindowsActionLeaseException and not OperationCanceledException,
            };
            reason = evidence.PermissionGranted ? ActionSafetyReason.Interrupted : WindowsActionFileNative.Reason(exception);
            if (!evidence.PermissionGranted) evidence = evidence with { ValidationReason = reason };
        }
        finally
        {
            // Unregister before releasing COM objects. Every acquired resource is
            // independently cleaned even if an earlier cleanup step fails.
            if (advised && driver is not null)
            {
                try { if (driver.Unadvise() < 0) MarkInterrupted(); }
                catch (Exception) { MarkInterrupted(); }
            }
            if (driver is not null)
            {
                try { driver.Dispose(); }
                catch (Exception) { MarkInterrupted(); }
            }
            evidence = evidence with { NamespaceHandoffOccurred = lease?.NamespaceHandoffOccurred ?? evidence.NamespaceHandoffOccurred };
            CaptureSink();
            // Re-protection uses the original approved envelope and no cancelable
            // token. A missing/replaced/unavailable path is never affirmative proof.
            if (lease is not null)
            {
                if ((evidence.NamespaceHandoffOccurred != false || evidence.PermissionGranted) && evidence.RunnerInterrupted != true
                    && RecycleBinClassifier.Classify(evidence, reason).Outcome == RecycleBinOutcomeKind.UnknownPendingRevalidation
                    && RecycleBinClassifier.Classify(evidence, reason).Reason != ActionSafetyReason.CallbackContradiction)
                {
                    try { evidence = evidence with { OriginalObjectProvenUnchanged = lease.ProveOriginalUnchanged() }; }
                    catch (Exception) { /* Failed proof leaves uncertainty; it is not a callback exception. */ }
                }
                try { lease.Dispose(); }
                catch (Exception) { MarkInterrupted(); }
            }
            if (initialized && driver is not null)
            {
                try { driver.UninitializeCom(); }
                catch (Exception) { MarkInterrupted(); }
            }
        }
        return RecycleBinClassifier.Classify(evidence, reason);

        void CaptureSink()
        {
            if (sink is null) return;
            evidence = sink.Evidence with
            {
                PreparationHResult = evidence.PreparationHResult,
                ValidationReason = sink.Evidence.ValidationReason ?? evidence.ValidationReason,
                PerformOperationsHResult = evidence.PerformOperationsHResult,
                GetAbortedHResult = evidence.GetAbortedHResult,
                OperationsAborted = evidence.OperationsAborted,
                RunnerInterrupted = evidence.RunnerInterrupted,
                NamespaceHandoffOccurred = evidence.NamespaceHandoffOccurred,
                PerformOperationsEntered = evidence.PerformOperationsEntered,
            };
        }

        void Prepare(int result)
        {
            evidence = evidence with { PreparationHResult = result };
            if (result < 0) throw new PreparationException(result);
        }
        void MarkInterrupted()
        {
            evidence = evidence with { RunnerInterrupted = true };
            reason = ActionSafetyReason.Interrupted;
        }
    }

    private static RecycleBinEvidence EmptyEvidence() => new(0, 0, false, null, null, 0, 0, null, false, false, false)
    {
        SelectedHandleReleased = false,
        RunnerInterrupted = false,
        NamespaceHandoffOccurred = false,
        PerformOperationsEntered = false,
    };
    private sealed class PreparationException : Exception
    {
        internal PreparationException(int result) { HResult = result; }
    }
}
