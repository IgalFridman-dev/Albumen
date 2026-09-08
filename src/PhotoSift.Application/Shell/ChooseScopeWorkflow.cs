using System.Runtime.ExceptionServices;
using PhotoSift.Application.Scope;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public sealed class ChooseScopeWorkflow : IChooseScopeWorkflow
{
    private const string RuleSetNotConfigured = "Not configured";
    private readonly WorkflowInbox inbox;
    private readonly IWorkflowScheduler scheduler;
    private readonly ILocalFolderPicker folderPicker;
    private readonly ILocalScopePreflight preflight;
    private ShellSnapshot current;
    private ScopeRequest? pendingPicker;
    private ScopeOperation? currentOperation;

    public ChooseScopeWorkflow(
        ILocalFolderPicker folderPicker,
        ILocalScopePreflight preflight,
        IWorkflowScheduler? scheduler = null)
    {
        this.folderPicker = folderPicker
            ?? throw new ArgumentNullException(nameof(folderPicker));
        this.preflight = preflight
            ?? throw new ArgumentNullException(nameof(preflight));
        this.scheduler = scheduler ?? new SynchronizationContextWorkflowScheduler();
        inbox = new WorkflowInbox(this.scheduler);
        current = new ShellSnapshot(
            InitialWorkflow.Create(),
            "Choose one local folder to scan.",
            ScopeSessionState.Cold,
            null,
            null,
            RuleSetNotConfigured,
            0);
    }

    public event EventHandler<ShellSnapshot>? SnapshotChanged;

    public ShellSnapshot Current => Volatile.Read(ref current);

    public async Task HandleAsync(
        ChooseScopeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await SubmitAsync(request =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var precedingPicker = pendingPicker;
            pendingPicker = request;
            precedingPicker?.RequestCancellation();
            // Opening a dialog is not an accepted replacement scope. The active
            // preflight remains relevant until a new selection is accepted.
            return () => PickAndPreflightAsync(request);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAsync(
        ChangeScopeRecursionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await SubmitAsync(request =>
        {
            var currentSelection = current.Selection;
            if (currentSelection is null)
            {
                return null;
            }

            var recursion = command.IncludeSubfolders
                ? ScopeRecursion.IncludeSubfolders
                : ScopeRecursion.TopLevelOnly;
            if (currentSelection.Recursion == recursion)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var operation = Accept(request,
                new ScopeSelection(currentSelection.SelectedPath, recursion));
            return () => RunPreflightAsync(operation);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleAsync(
        RetryScopePreflightCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await SubmitAsync(request =>
        {
            var selection = current.Selection
                ?? throw new InvalidOperationException(
                    "Scope preflight cannot be retried before a Scope is selected.");
            cancellationToken.ThrowIfCancellationRequested();
            var operation = Accept(request, selection);
            return () => RunPreflightAsync(operation);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SubmitAsync(
        Func<ScopeRequest, Func<Task>?> begin,
        CancellationToken cancellationToken)
    {
        var request = new ScopeRequest(cancellationToken);
        Exception? failure = null;
        try
        {
            var effect = await inbox.InvokeAsync(() =>
            {
                request.RegisterCallerCancellation(() =>
                    _ = inbox.InvokeAsync(request.RequestCancellation));
                return begin(request);
            }).ConfigureAwait(false);

            // Never hold a coordinator turn across an external operation, even
            // when its Task completes synchronously or it opens a modal dialog.
            if (effect is not null)
            {
                await effect().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            var cancellation = await inbox.InvokeAsync(() =>
            {
                if (ReferenceEquals(pendingPicker, request))
                {
                    pendingPicker = null;
                }

                return request.FinishWork();
            }).ConfigureAwait(false);
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            finally
            {
                await inbox.InvokeAsync(request.Dispose).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task PickAndPreflightAsync(ScopeRequest request)
    {
        string? selectedPath;
        try
        {
            // This dispatch is deliberately outside the inbox. WPF's modal
            // picker can pump UI messages without reentering a state transition.
            var picking = new TaskCompletionSource<Task<string?>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.Post(() =>
            {
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    picking.SetResult(folderPicker.PickSingleAsync(request.Token));
                }
                catch (Exception exception)
                {
                    picking.SetException(exception);
                }
            });
            selectedPath = await picking.Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested &&
            !request.CallerCancellation.IsCancellationRequested)
        {
            // Superseding a picker cancels only that interaction, not the scope
            // that remains selected behind it.
            return;
        }

        var operation = await inbox.InvokeAsync(() =>
        {
            if (!ReferenceEquals(pendingPicker, request))
            {
                return null;
            }

            pendingPicker = null;
            if (selectedPath is null || request.Token.IsCancellationRequested ||
                request.CallerCancellation.IsCancellationRequested)
            {
                return null;
            }

            return Accept(request, new ScopeSelection(selectedPath, ScopeRecursion.TopLevelOnly));
        }).ConfigureAwait(false);

        if (operation is not null)
        {
            await RunPreflightAsync(operation).ConfigureAwait(false);
        }
    }

    // Inbox-only. Identity and Checking state are committed before callbacks can
    // run; callbacks can submit a request but cannot execute a nested transition.
    private ScopeOperation Accept(ScopeRequest request, ScopeSelection selection)
    {
        var operation = new ScopeOperation(request, selection, current.Revision + 1);
        var precedingOperation = currentOperation;
        var precedingPicker = pendingPicker;
        var checking = CreateChecking(selection, operation.Revision);
        currentOperation = operation;
        pendingPicker = null;
        Volatile.Write(ref current, checking);

        precedingOperation?.Request.RequestCancellation();
        precedingPicker?.RequestCancellation();
        SnapshotChanged?.Invoke(this, checking);
        return operation;
    }

    private async Task RunPreflightAsync(ScopeOperation operation)
    {
        ScopePreflightEvidence? evidence = null;
        var canceled = false;
        try
        {
            operation.Request.Token.ThrowIfCancellationRequested();
            evidence = await preflight.InspectAsync(
                operation.Selection, operation.Request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        // Policy work and unexpected failures stay outside the owner turn. A
        // subscriber's exception is never reclassified as worker cancellation.
        var result = canceled ? null : ScopePreflightPolicy.Evaluate(evidence!);
        await inbox.InvokeAsync(() =>
        {
            if (!ReferenceEquals(currentOperation, operation))
            {
                return;
            }

            var completed = canceled || operation.Request.Token.IsCancellationRequested ||
                operation.Request.CallerCancellation.IsCancellationRequested
                ? CreateCanceled(operation.Selection, operation.Revision)
                : CreateCompleted(operation.Selection, result!, operation.Revision);

            // No await or external call separates the relevance check and commit.
            Volatile.Write(ref current, completed);
            SnapshotChanged?.Invoke(this, completed);
        }).ConfigureAwait(false);
    }

    private static ShellSnapshot CreateChecking(
        ScopeSelection selection,
        long revision) =>
        new(
            InitialWorkflow.Create(),
            "Checking location and access.",
            ScopeSessionState.Checking,
            selection,
            null,
            RuleSetNotConfigured,
            revision);

    private static ShellSnapshot CreateCompleted(
        ScopeSelection selection,
        ScopePreflightResult result,
        long revision) =>
        new(
            InitialWorkflow.Create(),
            result.Decision is ScopePreflightDecision.Ready
                ? "Scope is ready."
                : "Scope is blocked.",
            result.Decision is ScopePreflightDecision.Ready
                ? ScopeSessionState.Ready
                : ScopeSessionState.Blocked,
            selection,
            result,
            RuleSetNotConfigured,
            revision);

    private static ShellSnapshot CreateCanceled(
        ScopeSelection selection,
        long revision) =>
        new(
            InitialWorkflow.Create(),
            "Scope was not verified.",
            ScopeSessionState.Canceled,
            selection,
            null,
            RuleSetNotConfigured,
            revision);

}
