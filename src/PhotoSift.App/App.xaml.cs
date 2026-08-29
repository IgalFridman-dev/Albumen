using System.Windows;
using PhotoSift.App.Composition;
using PhotoSift.App.Shell;

namespace PhotoSift.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = CompositionRoot.CreateShellViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            try
            {
                Console.Out.WriteLine(ShellSmokeProbe.Validate(window.DataContext));
                Shutdown(exitCode: 0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"PHOTOSIFT_SMOKE_FAIL type={exception.GetType().Name}");
                Shutdown(exitCode: 1);
            }

            return;
        }

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
