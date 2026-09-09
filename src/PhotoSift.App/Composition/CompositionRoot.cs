using System.IO;
using PhotoSift.App.Shell;
using PhotoSift.Analysis.Exact;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Infrastructure.Sqlite.Review;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Shell;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.App.Composition;

public static class CompositionRoot
{
    public static ShellViewModel CreateShellViewModel()
    {
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
        var scan = new ScanCoordinator(scheduler, preflight, executor);
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoSift", "photosift.db");
        var evidenceStore = new SqliteOverrideEvidenceStore(databasePath);
        var review = new ReviewCoordinator(scheduler,
            new BoundedReviewRevalidator(reader, analyzer), evidenceStore);
        var photoWorkflow = new PhotoSiftWorkflow(workflow, scan, scheduler, review);
        var dialog = new WpfReviewDialogService(() => System.Windows.Application.Current?.MainWindow);
        return new ShellViewModel(workflow, photoWorkflow, dialog);
    }
}

