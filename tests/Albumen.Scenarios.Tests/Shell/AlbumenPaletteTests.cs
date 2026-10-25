using System.Globalization;
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

    // Tuned after the b9e6761 walkthrough (Igal, 2026-10-25): the grounds, fields and fills sit a
    // step lighter, the step rail has its own navy panel, and the text, accent and accent-line
    // tokens are exactly as they were.
    [Fact]
    public void TheDarkTonesSitAStepLighterAndTheTextAndAccentStay()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var dark = new ResourceDictionary();
            AlbumenPalette.Apply(dark, highContrast: false);

            Assert.Equal("#FF121212", ColorOf(dark, "Albumen.WindowBrush"));
            Assert.Equal("#FF1E1E1E", ColorOf(dark, "Albumen.SurfaceBrush"));
            Assert.Equal("#FF262626", ColorOf(dark, "Albumen.FieldBrush"));
            Assert.Equal("#FF2D2D2D", ColorOf(dark, "Albumen.SubtleFillBrush"));
            Assert.Equal("#FF353535", ColorOf(dark, "Albumen.HoverBrush"));
            Assert.Equal("#FF3E3E3E", ColorOf(dark, "Albumen.PressedBrush"));
            Assert.Equal("#FF3E3E3E", ColorOf(dark, "Albumen.PillBrush"));
            Assert.Equal("#FF4B4B4B", ColorOf(dark, "Albumen.PillHoverBrush"));
            Assert.Equal("#FF313131", ColorOf(dark, "Albumen.BorderBrush"));
            Assert.Equal("#FF414141", ColorOf(dark, "Albumen.BorderStrongBrush"));
            Assert.Equal("#FF141821", ColorOf(dark, "Albumen.RailBrush"));

            Assert.Equal("#FFDEDEDE", ColorOf(dark, "Albumen.TextBrush"));
            Assert.Equal("#FF9D9D9D", ColorOf(dark, "Albumen.MutedTextBrush"));
            Assert.Equal("#FF3D7FE6", ColorOf(dark, "Albumen.AccentBrush"));
            Assert.Equal("#FF1B2A44", ColorOf(dark, "Albumen.AccentSoftBrush"));
            var line = Assert.IsType<LinearGradientBrush>(dark["Albumen.AccentLineBrush"]);
            Assert.Equal(["#FF3B82F6", "#FF8B5CF6"],
                line.GradientStops.Select(stop => stop.Color.ToString(CultureInfo.InvariantCulture)).ToArray());
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

    // A keyed control style skips Fluent's dark implicit style, so the control falls back to the
    // classic theme's black text, and a tab passes that black to its whole page (the e363606
    // walkthrough, Igal, 2026-10-25). Each such style names its text brush.
    [Fact]
    public void EveryKeyedControlStyleNamesItsTextBrush()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var controls = typeof(System.Windows.Controls.Control).Assembly;
        var offenders = XamlFiles()
            .SelectMany(file => XDocument.Load(file).Descendants(presentation + "Style"))
            .Where(style => (string?)style.Attribute(Xaml + "Key") is { } key && !key.EndsWith("FocusVisual", StringComparison.Ordinal)
                && style.Attribute("BasedOn") is null
                && controls.GetType($"System.Windows.Controls.{(string?)style.Attribute("TargetType")}") is { } type
                && type.IsSubclassOf(typeof(System.Windows.Controls.Control)))
            .Where(style => !style.Elements(presentation + "Setter").Any(setter => (string?)setter.Attribute("Property") == "Foreground"))
            .Select(style => (string)style.Attribute(Xaml + "Key")!)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string[] Keys(ResourceDictionary dictionary) =>
        dictionary.Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray();

    private static string ColorOf(ResourceDictionary dictionary, string key) =>
        Assert.IsType<SolidColorBrush>(dictionary[key]).Color.ToString(CultureInfo.InvariantCulture);

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
