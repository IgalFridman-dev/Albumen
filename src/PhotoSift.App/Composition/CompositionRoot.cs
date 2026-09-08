using PhotoSift.App.Shell;
using PhotoSift.Analysis.Exact;
using PhotoSift.Application.Scan;
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
        var executor = new BoundedScanExecutor(new WindowsScanEnumerator(),
            new WindowsProtectedFileReader(), new StreamingExactContentAnalyzer());
        var scan = new ScanCoordinator(scheduler, preflight, executor);
        return new ShellViewModel(workflow, new PhotoSiftWorkflow(workflow, scan, scheduler));
    }
}

