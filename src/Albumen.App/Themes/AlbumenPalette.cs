using System.Windows;
using System.Windows.Media;

namespace Albumen.App.Themes;

// The one place Albumen chooses a colour. Views paint through DynamicResource
// Albumen.*Brush keys; Apply fills them with the dark palette (sampled from the Windows
// Photos gallery), or with the system brushes under High Contrast so the user's contrast
// theme wins.
internal static class AlbumenPalette
{
    private static readonly Token[] Tokens =
    [
        new("Albumen.WindowBrush", Solid(0x0A0A0A), () => SystemColors.WindowBrush),
        new("Albumen.SurfaceBrush", Solid(0x171717), () => SystemColors.WindowBrush),
        new("Albumen.FieldBrush", Solid(0x1F1F1F), () => SystemColors.WindowBrush),
        new("Albumen.SubtleFillBrush", Solid(0x262626), () => SystemColors.ControlBrush),
        new("Albumen.HoverBrush", Solid(0x2E2E2E), () => SystemColors.ControlBrush),
        new("Albumen.PressedBrush", Solid(0x373737), () => SystemColors.ControlBrush),
        new("Albumen.PillBrush", Solid(0x373737), () => SystemColors.ControlBrush),
        new("Albumen.PillHoverBrush", Solid(0x444444), () => SystemColors.ControlBrush),
        new("Albumen.BorderBrush", Solid(0x2A2A2A), () => SystemColors.ActiveBorderBrush),
        new("Albumen.BorderStrongBrush", Solid(0x3A3A3A), () => SystemColors.WindowTextBrush),
        new("Albumen.TextBrush", Solid(0xDEDEDE), () => SystemColors.WindowTextBrush),
        new("Albumen.MutedTextBrush", Solid(0x9D9D9D), () => SystemColors.WindowTextBrush),
        new("Albumen.AccentBrush", Solid(0x3D7FE6), () => SystemColors.HighlightBrush),
        new("Albumen.AccentHoverBrush", Solid(0x4A8BF0), () => SystemColors.HighlightBrush),
        new("Albumen.AccentPressedBrush", Solid(0x3470CC), () => SystemColors.HighlightBrush),
        new("Albumen.OnAccentBrush", Solid(0xFFFFFF), () => SystemColors.HighlightTextBrush),
        new("Albumen.AccentSoftBrush", Solid(0x1B2A44), () => SystemColors.WindowBrush),
        new("Albumen.SuccessBrush", Solid(0x4ADE80), () => SystemColors.WindowTextBrush),
        new("Albumen.SuccessSoftBrush", Solid(0x12291B), () => SystemColors.WindowBrush),
        new("Albumen.WarningBrush", Solid(0xF5B971), () => SystemColors.WindowTextBrush),
        new("Albumen.WarningSoftBrush", Solid(0x2E2210), () => SystemColors.WindowBrush),
        new("Albumen.DangerBrush", Solid(0xE5484D), () => SystemColors.WindowTextBrush),
        new("Albumen.DangerHoverBrush", Solid(0xC73A3F), () => SystemColors.WindowTextBrush),
        new("Albumen.DangerSoftBrush", Solid(0x2F1517), () => SystemColors.WindowBrush),
        // The 3 px line that marks what is selected or current: blue at the top, violet below.
        new("Albumen.AccentLineBrush", Gradient(0x3B82F6, 0x8B5CF6), () => SystemColors.HighlightBrush),
    ];

    // System brushes are read on every call: they change with the contrast theme.
    public static void Apply(ResourceDictionary dictionary, bool highContrast)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        foreach (var token in Tokens)
            dictionary[token.Key] = highContrast ? token.HighContrast() : token.Dark;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush Solid(int rgb)
    {
        var brush = new SolidColorBrush(Rgb(rgb));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(int top, int bottom)
    {
        var brush = new LinearGradientBrush(Rgb(top), Rgb(bottom), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private sealed record Token(string Key, Brush Dark, Func<Brush> HighContrast);
}
