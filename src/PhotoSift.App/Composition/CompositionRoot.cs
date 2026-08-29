using PhotoSift.App.Shell;
using PhotoSift.Application.Shell;

namespace PhotoSift.App.Composition;

public static class CompositionRoot
{
    public static ShellViewModel CreateShellViewModel() =>
        new(new NativeOfflineShell());
}
