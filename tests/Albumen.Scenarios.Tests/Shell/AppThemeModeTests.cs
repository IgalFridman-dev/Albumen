using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class AppThemeModeTests
{
    [Fact]
    public void FluentThemeStaysLightToMatchSystemColorsSurfaces()
    {
        // MainWindow and the dialogs paint backgrounds and text with SystemColors
        // brushes, which do not follow Windows dark mode. A System or Dark Fluent
        // theme renders white control text on those light surfaces.
        var appXaml = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Albumen.App", "App.xaml"));

        Assert.Equal("Light", (string?)appXaml.Root?.Attribute("ThemeMode"));
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
