using System.IO;
using PhotoSift.App.Shell;
using PhotoSift.Analysis.Exact;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Application.Action;
using PhotoSift.Infrastructure.Windows.Action;
using PhotoSift.Infrastructure.Sqlite;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Workflow;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.App.Composition;

public static class CompositionRoot
{
    public static ShellViewModel CreateShellViewModel()
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoSift", "photosift.db");
        return CreateShellViewModel(databasePath, new WindowsRecycleBinExecutor());
    }

    internal static ShellViewModel CreateShellViewModel(
        string databasePath, IRecycleBinExecutor recycleBinExecutor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(recycleBinExecutor);
        ILocalFolderPicker picker = new WpfLocalFolderPicker(
            () => System.Windows.Application.Current?.MainWindow);
        ILocalScopePreflight preflight = new WindowsLocalScopePreflight();
        var scheduler = new DispatcherWorkflowScheduler(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        IChooseScopeWorkflow workflow = new ChooseScopeWorkflow(
            picker,
            preflight,
            scheduler);
        var reader = new WindowsProtectedFileReader();
        var analyzer = new StreamingExactContentAnalyzer();
        var executor = new BoundedScanExecutor(new WindowsScanEnumerator(), reader, analyzer);
        var stores = new SqlitePhotoSiftStores(databasePath);
        var scan = new ScanCoordinator(
            scheduler, preflight, executor, stores.ScanIdentities, stores.ScanCheckpoints);
        var review = new ReviewCoordinator(scheduler,
            new BoundedReviewRevalidator(reader, analyzer), stores.OverrideEvidence,
            actionSafety: new ProtectedActionSafetyValidator(reader, analyzer));
        var action = new ActionCoordinator(scheduler, review, stores.OverrideEvidence,
            stores.ActionJournal, recycleBinExecutor);
        var recovery = new ActionRecoveryCoordinator(scheduler, stores.ActionRecords,
            new WindowsActionRevalidationProbe());
        var photoWorkflow = new OwnedPhotoSiftWorkflow(
            new PhotoSiftWorkflow(workflow, scan, scheduler, review, action, recovery),
            recovery,
            scan,
            action,
            review,
            stores);
        var dialog = new WpfReviewDialogService(() => System.Windows.Application.Current?.MainWindow);
        return new ShellViewModel(workflow, photoWorkflow, dialog, stores.ScanData);
    }

    private sealed class OwnedPhotoSiftWorkflow(
        IPhotoSiftWorkflow workflow,
        IAsyncDisposable? recovery,
        IAsyncDisposable scan,
        IAsyncDisposable action,
        IAsyncDisposable review,
        IAsyncDisposable evidenceStore) : IPhotoSiftWorkflow, IAsyncDisposable
    {
        private OwnedPhotoSiftWorkflow(IPhotoSiftWorkflow workflow, IAsyncDisposable scan,
            IAsyncDisposable action, IAsyncDisposable review, IAsyncDisposable evidenceStore)
            : this(workflow, null, scan, action, review, evidenceStore)
        {
        }

        private readonly object lifetime = new();
        private Task? disposalTask;

        public PhotoSiftSnapshot Current => workflow.Current;

        public event EventHandler<PhotoSiftSnapshot>? SnapshotChanged
        {
            add => workflow.SnapshotChanged += value;
            remove => workflow.SnapshotChanged -= value;
        }

        public Task ContinueAsync() => workflow.ContinueAsync();
        public Task ConfirmExactRulesAsync() => workflow.ConfirmExactRulesAsync();
        public Task StartScanAsync() => workflow.StartScanAsync();
        public Task CancelScanAsync() => workflow.CancelScanAsync();
        public Task OpenReviewAsync() => workflow.OpenReviewAsync();
        public Task<ReviewCommandResult> ChangeReviewSelectionAsync(
            SelectionCommand command,
            CancellationToken cancellationToken) =>
            workflow.ChangeReviewSelectionAsync(command, cancellationToken);
        public Task<ReviewCommandResult> AffirmReviewOverrideAsync(
            OverrideAffirmationCommand command,
            CancellationToken cancellationToken) =>
            workflow.AffirmReviewOverrideAsync(command, cancellationToken);
        public Task<ReviewCommandResult> ApproveReviewAsync(
            ApproveSelectionCommand command,
            CancellationToken cancellationToken) =>
            workflow.ApproveReviewAsync(command, cancellationToken);
        public Task RevalidateUnknownAsync(Guid requestId, CancellationToken cancellationToken) =>
            workflow.RevalidateUnknownAsync(requestId, cancellationToken);
        public Task NavigateAsync(WorkflowPhase phase) => workflow.NavigateAsync(phase);

        public ValueTask DisposeAsync()
        {
            lock (lifetime)
                return new(disposalTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception>? failures = null;
            try
            {
                if (recovery is not null) await recovery.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                // Action disposal includes admitted acknowledgments, Windows STA
                // settlement and retained leases. The dispatcher is still alive.
                await action.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                await review.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            try
            {
                await scan.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await evidenceStore.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures?.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException(failures);
        }
    }
}
