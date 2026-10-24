using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Albumen.App.Themes;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class AlbumenPaletteTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex DynamicToken = new(@"DynamicResource\s+(?<key>Albumen\.\w+Brush)\}", RegexOptions.CultureInvariant);
    private static readonly Regex StaticToken = new(@"StaticResource\s+Albumen\.\w+Brush\}", RegexOptions.CultureInvariant);

    // The control templates that change shape as well as colour under High Contrast; only
    // they may name system colours, everything else paints through the palette tokens.
    private static readonly string[] HighContrastTemplates =
    [
        "Albumen.HighContrastButtonTemplate",
        "Albumen.HighContrastSwitchTemplate",
        "Albumen.HighContrastStepTemplate",
        "Albumen.HighContrastCardTemplate",
        "Albumen.HighContrastTopBarItemTemplate",
    ];

    [Fact]
    public void EveryTokenUsedByTheViewsExistsInBothPalettes()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var sources = XamlFiles().Select(File.ReadAllText).ToArray();
            var used = sources
                .SelectMany(source => DynamicToken.Matches(source))
                .Select(match => match.Groups["key"].Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("Albumen.AccentBrush", used);
            // The palette is written by code, so a StaticResource lookup would fail at load.
            Assert.All(sources, source => Assert.DoesNotMatch(StaticToken, source));

            var dark = new ResourceDictionary();
            AlbumenPalette.Apply(dark, highContrast: false);
            var contrast = new ResourceDictionary();
            AlbumenPalette.Apply(contrast, highContrast: true);

            Assert.All(used, key =>
            {
                Assert.True(dark.Contains(key), $"{key} is missing from the dark palette.");
                Assert.True(contrast.Contains(key), $"{key} is missing from the High Contrast palette.");
            });
            Assert.Equal(Keys(dark), Keys(contrast));
            Assert.All(Keys(dark), key => Assert.True(Assert.IsAssignableFrom<Brush>(dark[key]).IsFrozen));
        });
    }

    [Fact]
    public void HighContrastMapsEveryTokenToASystemBrush()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var contrast = new ResourceDictionary();
            AlbumenPalette.Apply(contrast, highContrast: true);
            var systemBrushes = typeof(SystemColors)
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => property.PropertyType == typeof(SolidColorBrush))
                .Select(property => property.GetValue(null))
                .ToArray();

            Assert.NotEmpty(Keys(contrast));
            Assert.All(Keys(contrast), key => Assert.Contains(contrast[key], systemBrushes));
        });
    }

    [Fact]
    public void NoViewUsesSystemColorsDirectly()
    {
        var offenders = XamlFiles()
            .SelectMany(file => XDocument.Load(file).Descendants()
                .SelectMany(element => element.Attributes())
                .Where(attribute => attribute.Value.Contains("SystemColors.", StringComparison.Ordinal))
                .Where(attribute => !attribute.Parent!.AncestorsAndSelf().Any(element =>
                    (string?)element.Attribute(Xaml + "Key") is { } key
                    && HighContrastTemplates.Contains(key, StringComparer.Ordinal)))
                .Select(attribute => $"{Path.GetFileName(file)}: {attribute.Parent!.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\""))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string[] Keys(ResourceDictionary dictionary) =>
        dictionary.Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray();

    private static IEnumerable<string> XamlFiles()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "Albumen.App");
        return Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
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
