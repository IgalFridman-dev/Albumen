using System.IO;
using System.Windows;
using Albumen.App.Composition;
using Albumen.App.Shell;

namespace Albumen.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = CompositionRoot.CreateShellViewModel();
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            var exitCode = await RunSmokeAsync(viewModel, ShellSmokeProbe.Validate, Console.Out, Console.Error);
            Shutdown(exitCode);
            return;
        }

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    public static async Task<int> RunSmokeAsync(
        object viewModel,
        Func<object?, string> probe,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string? success = null;
        Exception? failure = null;
        try { success = probe(viewModel); }
        catch (Exception exception) { failure = exception; }

        if (viewModel is IAsyncDisposable lifetime)
        {
            try { await lifetime.DisposeAsync(); }
            catch (Exception exception) { failure ??= exception; }
        }

        if (failure is not null)
        {
            error.WriteLine($"ALBUMEN_SMOKE_FAIL type={failure.GetType().Name}");
            return 1;
        }

        output.WriteLine(success);
        return 0;
    }
}
