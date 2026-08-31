using PhotoSift.App.Shell;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Shell;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.App.Composition;

public static class CompositionRoot
{
    public static ShellViewModel CreateShellViewModel()
    {
        ILocalFolderPicker picker = new WpfLocalFolderPicker(
            () => System.Windows.Application.Current.MainWindow);
        ILocalScopePreflight preflight = new WindowsLocalScopePreflight();
        IChooseScopeWorkflow workflow = new ChooseScopeWorkflow(picker, preflight);
        return new ShellViewModel(workflow);
    }
}
