using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Infrastructure.Windows.Preview;

namespace Albumen.App.Shell;

public sealed class PreviewItemViewModel
{
    public PreviewItemViewModel(PreviewDecodeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        FullPath = result.FullPath;
        StatusText = result.Availability is PreviewAvailability.Available
            ? "Preview available."
            : result.Reason;
        AutomationName = $"{FullPath}. {StatusText}";
    }

    public PreviewItemViewModel(ReviewPreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        FullPath = result.FullPath;
        StatusText = result.Availability is PreviewAvailability.Available
            ? "Preview available."
            : result.Reason;
        Image = CreateImage(result.Frame);
        AutomationName = $"{FullPath}. {StatusText}";
    }

    public string FullPath { get; }
    public string StatusText { get; }
    public string AutomationName { get; }
    public ImageSource? Image { get; }

    internal static BitmapSource? CreateImage(PreviewFrame? frame)
    {
        if (frame is null) return null;
        var image = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.DisplayPixels.ToArray(),
            checked(frame.Width * 4));
        image.Freeze();
        return image;
    }
}
