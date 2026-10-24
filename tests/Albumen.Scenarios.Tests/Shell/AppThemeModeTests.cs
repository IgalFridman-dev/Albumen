using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class AppThemeModeTests
{
    [Fact]
    public void FluentThemeIsDarkToMatchTheAlbumenPalette()
    {
        // MainWindow and the dialogs paint surfaces and text with the Albumen palette
        // tokens, which are dark (system brushes under High Contrast), so Fluent's dark
        // controls agree with the window around them.
        var appXaml = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Albumen.App", "App.xaml"));

        Assert.Equal("Dark", (string?)appXaml.Root?.Attribute("ThemeMode"));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Albumen.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Albumen repository root.");
    }
}
