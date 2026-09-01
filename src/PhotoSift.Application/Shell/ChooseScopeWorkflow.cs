using System.Runtime.ExceptionServices;
using PhotoSift.Application.Scope;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public sealed class ChooseScopeWorkflow : IChooseScopeWorkflow
{
    private const string RuleSetNotConfigured = "Not configured";
    private readonly object gate = new();
    private readonly Queue<ShellSnapshot> pendingSnapshotChanges = new();
    private readonly ILocalFolderPicker folderPicker;
    private readonly ILocalScopePreflight preflight;
    private ShellSnapshot current;
    private bool isDeliveringSnapshotChanges;

    public ChooseScopeWorkflow(
        ILocalFolderPicker folderPicker,
        ILocalScopePreflight preflight)
    {
        this.folderPicker = folderPicker
            ?? throw new ArgumentNullException(nameof(folderPicker));
        this.preflight = preflight
            ?? throw new ArgumentNullException(nameof(preflight));
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

    public ShellSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public async Task HandleAsync(
        ChooseScopeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var selectedPath = await folderPicker
            .PickSingleAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selectedPath is null)
        {
            return;
        }

        var selection = new ScopeSelection(
            selectedPath,
            ScopeRecursion.TopLevelOnly);
        var start = BeginPreflight(selection);
        DeliverSnapshotChangesIfNeeded(start.ShouldDeliver);

        await RunPreflightAsync(selection, start.Revision, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task HandleAsync(
        ChangeScopeRecursionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ScopeSelection selection;
        (long Revision, bool ShouldDeliver) start;
        lock (gate)
        {
            var currentSelection = current.Selection;
            if (currentSelection is null)
            {
                return;
            }

            var recursion = command.IncludeSubfolders
                ? ScopeRecursion.IncludeSubfolders
                : ScopeRecursion.TopLevelOnly;
            if (currentSelection.Recursion == recursion)
            {
                return;
            }

            selection = new ScopeSelection(currentSelection.SelectedPath, recursion);
            start = BeginPreflightUnderLock(selection);
        }

        DeliverSnapshotChangesIfNeeded(start.ShouldDeliver);
        await RunPreflightAsync(selection, start.Revision, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task HandleAsync(
        RetryScopePreflightCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ScopeSelection selection;
        (long Revision, bool ShouldDeliver) start;
        lock (gate)
        {
            selection = current.Selection
                ?? throw new InvalidOperationException(
                    "Scope preflight cannot be retried before a Scope is selected.");
            start = BeginPreflightUnderLock(selection);
        }

        DeliverSnapshotChangesIfNeeded(start.ShouldDeliver);
        await RunPreflightAsync(selection, start.Revision, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunPreflightAsync(
        ScopeSelection selection,
        long revision,
        CancellationToken cancellationToken)
    {
        ScopePreflightEvidence evidence;
        try
        {
            evidence = await preflight
                .InspectAsync(selection, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishIfCurrent(revision, CreateCanceled(selection, revision));
            return;
        }

        var result = ScopePreflightPolicy.Evaluate(evidence);
        PublishIfCurrent(revision, CreateCompleted(selection, result, revision));
    }

    private (long Revision, bool ShouldDeliver) BeginPreflight(
        ScopeSelection selection)
    {
        lock (gate)
        {
            return BeginPreflightUnderLock(selection);
        }
    }

    private (long Revision, bool ShouldDeliver) BeginPreflightUnderLock(
        ScopeSelection selection)
    {
        var revision = current.Revision + 1;
        var checking = CreateChecking(selection, revision);
        current = checking;
        return (revision, EnqueueSnapshotChangeUnderLock(checking));
    }

    private void PublishIfCurrent(long revision, ShellSnapshot completed)
    {
        bool shouldDeliver;
        lock (gate)
        {
            if (current.Revision != revision)
            {
                return;
            }

            current = completed;
            shouldDeliver = EnqueueSnapshotChangeUnderLock(completed);
        }

        DeliverSnapshotChangesIfNeeded(shouldDeliver);
    }

    private bool EnqueueSnapshotChangeUnderLock(ShellSnapshot snapshot)
    {
        pendingSnapshotChanges.Enqueue(snapshot);
        if (isDeliveringSnapshotChanges)
        {
            return false;
        }

        isDeliveringSnapshotChanges = true;
        return true;
    }

    private void DeliverSnapshotChangesIfNeeded(bool shouldDeliver)
    {
        if (!shouldDeliver)
        {
            return;
        }

        ExceptionDispatchInfo? deliveryFailure = null;
        while (true)
        {
            ShellSnapshot snapshot;
            lock (gate)
            {
                if (pendingSnapshotChanges.Count == 0)
                {
                    isDeliveringSnapshotChanges = false;
                    break;
                }

                snapshot = pendingSnapshotChanges.Dequeue();
            }

            try
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }
            catch (Exception exception)
            {
                deliveryFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        deliveryFailure?.Throw();
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
