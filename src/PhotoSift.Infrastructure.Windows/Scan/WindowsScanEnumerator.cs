using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.Infrastructure.Windows.Scan;

public sealed class WindowsScanEnumerator : IScanEnumerator
{
    private readonly WindowsScopeTraversal traversal;

    public WindowsScanEnumerator()
        : this(new WindowsScopeNative(
            new WicCodecCapability(new ComClassAvailability())))
    {
    }

    internal WindowsScanEnumerator(IWindowsScopeNative native)
    {
        traversal = new WindowsScopeTraversal(
            native ?? throw new ArgumentNullException(nameof(native)));
    }

    public async IAsyncEnumerable<ScanEnumerationEvent> EnumerateAsync(
        ScanRunContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await Task.CompletedTask.ConfigureAwait(false);

        using var iterator = traversal
            .Walk(context.Scope.Selection, cancellationToken)
            .GetEnumerator();

        while (true)
        {
            WindowsTraversalEvent? current = null;
            Exception? failure = null;
            bool hasNext;
            try
            {
                hasNext = iterator.MoveNext();
                if (hasNext)
                {
                    current = iterator.Current;
                }
            }
            catch (Exception exception)
            {
                hasNext = false;
                failure = exception;
            }

            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                yield return new ScanEnumerationEnded(
                    new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled));
                yield break;
            }

            if (failure is not null)
            {
                yield return new ScanEnumerationEnded(MapUnexpectedFailure(failure));
                yield break;
            }

            if (!hasNext)
            {
                yield return new ScanEnumerationEnded(
                    new StageOutcome(
                        ScanStageState.FailedTerminal,
                        ScanReason.ProgrammingFault));
                yield break;
            }

            switch (current)
            {
                case WindowsTraversalRoot root:
                    if (RootChanged(context.Scope, root.Evidence.Scope))
                    {
                        yield return new ScanEnumerationEnded(
                            new StageOutcome(
                                ScanStageState.FailedRetryable,
                                ScanReason.Changed));
                        yield break;
                    }

                    break;
                case WindowsTraversalFile file:
                    yield return new ScanFileFound(file.File);
                    break;
                case WindowsTraversalFinished finished:
                    yield return new ScanEnumerationEnded(MapFinished(finished.Evidence));
                    yield break;
                default:
                    yield return new ScanEnumerationEnded(
                        new StageOutcome(
                            ScanStageState.FailedTerminal,
                            ScanReason.ProgrammingFault));
                    yield break;
            }
        }
    }

    private static bool RootChanged(
        ResolvedScope expected,
        ResolvedScope? observed) =>
        observed is not null &&
        (observed.RootIdentity != expected.RootIdentity ||
         !string.Equals(
             observed.ResolvedPath,
             expected.ResolvedPath,
             StringComparison.OrdinalIgnoreCase));

    private static StageOutcome MapFinished(ScopePreflightEvidence evidence)
    {
        if (ScopePreflightPolicy.Evaluate(evidence).Decision is ScopePreflightDecision.Ready)
        {
            return new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        }

        if (evidence.Traversal is ScopeTraversalState.RootAccessDenied or
            ScopeTraversalState.DescendantAccessDenied)
        {
            return new StageOutcome(
                ScanStageState.FailedRetryable,
                ScanReason.AccessDenied);
        }

        if (evidence.Volume is ScopeVolumeKind.Unavailable)
        {
            return new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Missing);
        }

        if (evidence.Volume is ScopeVolumeKind.Remote or ScopeVolumeKind.Unsupported ||
            evidence.Cloud is ScopeCloudState.Synchronized or ScopeCloudState.Placeholder)
        {
            return new StageOutcome(
                ScanStageState.FailedTerminal,
                ScanReason.UnsafeLocation);
        }

        return new StageOutcome(
            ScanStageState.FailedTerminal,
            ScanReason.UnknownSafety);
    }

    private static StageOutcome MapUnexpectedFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException =>
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
        WindowsScopeNativeException { Kind: WindowsScopeFailureKind.AccessDenied } =>
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
        WindowsScopeNativeException { Kind: WindowsScopeFailureKind.Unavailable } =>
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Missing),
        IOException or Win32Exception =>
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
        _ => new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault),
    };
}
