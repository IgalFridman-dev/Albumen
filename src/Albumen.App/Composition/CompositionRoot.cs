using System.IO;
using Albumen.App.Shell;
using Albumen.Analysis.Exact;
using Albumen.Analysis.NearDuplicate;
using Albumen.Analysis.Sharpness;
using Albumen.Application.LocalData;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Application.Review;
using Albumen.Application.Action;
using Albumen.Infrastructure.Windows.Action;
using Albumen.Infrastructure.Onnx;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Application.Scope;
using Albumen.Application.Shell;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Workflow;
using Albumen.Infrastructure.Windows.Scope;
using Albumen.Infrastructure.Windows.Preview;

namespace Albumen.App.Composition;

public static class CompositionRoot
{
    public static ShellViewModel CreateShellViewModel()
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Albumen", "albumen.db");
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
        var stores = new SqliteAlbumenStores(databasePath);
        var nearDuplicates = new BoundedNearDuplicatePipeline(
            new WicNearFeatureSource(reader, analyzer, new WicPreviewDecoder(),
                new ManagedPerceptualSignature(), new ManagedSharpness()));
        var encoder = new OnnxVisionLanguageEncoder(Path.Combine(AppContext.BaseDirectory, "models"));
        var semanticIndex = new BoundedSemanticIndexer(encoder,
            new WicEncoderImageSource(reader, analyzer, new WicPreviewDecoder()),
            stores.Embeddings, Environment.ProcessorCount);
        var scan = new ScanCoordinator(scheduler, preflight, executor, stores.ScanIdentities,
            stores.ScanCheckpoints, nearDuplicates, semanticIndex);
        var review = new ReviewCoordinator(scheduler,
            new BoundedReviewRevalidator(reader, analyzer), stores.OverrideEvidence,
            actionSafety: new ProtectedActionSafetyValidator(reader, analyzer));
        var action = new ActionCoordinator(scheduler, review, stores.OverrideEvidence,
            stores.ActionJournal, recycleBinExecutor);
        var recovery = new ActionRecoveryCoordinator(scheduler, stores.ActionRecords,
            new WindowsActionRevalidationProbe());
        var photoWorkflow = new OwnedAlbumenWorkflow(
            new AlbumenWorkflow(workflow, scan, scheduler, review, action, recovery),
            recovery,
            scan,
            action,
            review,
            stores,
            encoder);
        var dialog = new WpfReviewDialogService(() => System.Windows.Application.Current?.MainWindow);
        // The viewer decodes large enough to stay sharp on high-DPI screens and when
        // zoomed; the cache keeps the two compared photos.
        var preview = new BoundedReviewPreviewSource(
            reader,
            new WicPreviewDecoder(),
            new BoundedThumbnailCache(160L * 1024 * 1024),
            maximumEdge: 4096);
        var thumbnails = new BoundedReviewPreviewSource(
            reader,
            new WicPreviewDecoder(),
            new BoundedThumbnailCache(32L * 1024 * 1024),
            maximumEdge: 320);
        return new ShellViewModel(
            workflow, photoWorkflow, dialog, new SemanticClearingScanData(stores.ScanData, scan),
            preview, thumbnails);
    }

    // Clearing the local Scan data removes the stored vectors too, so the shown Scan's
    // semantic state returns to not prepared. Nothing else about the Scan changes.
    private sealed class SemanticClearingScanData(ILocalScanDataControl inner, ScanCoordinator scan)
        : ILocalScanDataControl
    {
        public async ValueTask<ScanDataClearResult> ClearAsync(CancellationToken cancellationToken)
        {
            var result = await inner.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ScanDataClearOutcome.Cleared)
                await scan.ForgetSemanticIndexAsync().ConfigureAwait(false);
            return result;
        }
    }

    private sealed class OwnedAlbumenWorkflow(
        IAlbumenWorkflow workflow,
        IAsyncDisposable? recovery,
        IAsyncDisposable scan,
        IAsyncDisposable action,
        IAsyncDisposable review,
        IAsyncDisposable evidenceStore,
        IDisposable? encoder = null) : IAlbumenWorkflow, IAsyncDisposable
    {
        private OwnedAlbumenWorkflow(IAlbumenWorkflow workflow, IAsyncDisposable scan,
            IAsyncDisposable action, IAsyncDisposable review, IAsyncDisposable evidenceStore)
            : this(workflow, null, scan, action, review, evidenceStore)
        {
        }

        private readonly object lifetime = new();
        private Task? disposalTask;

        public AlbumenSnapshot Current => workflow.Current;

        public event EventHandler<AlbumenSnapshot>? SnapshotChanged
        {
            add => workflow.SnapshotChanged += value;
            remove => workflow.SnapshotChanged -= value;
        }

        public Task ContinueAsync() => workflow.ContinueAsync();
        public Task SetScanOptionsAsync(ScanOptions options) => workflow.SetScanOptionsAsync(options);
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
        public Task ContinueReviewAsync() => workflow.ContinueReviewAsync();

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
                // After the Scan: no indexing worker is still inside a model session.
                encoder?.Dispose();
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
