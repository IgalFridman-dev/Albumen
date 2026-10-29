using System.IO;
using System.Xml.Linq;
using Albumen.App;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class AppIconTests
{
    [Fact]
    public void TheWindowAndTheDialogCarryTheAppIcon()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var dialog = new SurvivorOverrideDialog(ReviewData.Proposal(ReviewData.CreateSnapshot()));
            try
            {
                Assert.NotNull(window.Icon);
                Assert.NotNull(dialog.Icon);
                Assert.True(window.Icon.Width > 0);
            }
            finally
            {
                dialog.Close();
                window.Close();
            }
        });
    }

    [Fact]
    public void TheExecutableEmbedsTheIcon()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Albumen.App");
        var project = XDocument.Load(Path.Combine(app, "Albumen.App.csproj"));

        var icon = Assert.Single(project.Descendants("ApplicationIcon")).Value;
        Assert.Equal(@"Assets\Albumen.ico", icon);
        Assert.True(File.Exists(Path.Combine(app, "Assets", "Albumen.ico")));
        // The windows load the same file as a resource for their title bar.
        Assert.Contains(project.Descendants("Resource"),
            resource => (string?)resource.Attribute("Include") == @"Assets\Albumen.ico");
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
