using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoSift.Infrastructure.Windows.Preview;

namespace PhotoSift.App.Shell;

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

    private static BitmapSource? CreateImage(PreviewFrame? frame)
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

public sealed class ReviewComparisonEvidenceViewModel
{
    internal ReviewComparisonEvidenceViewModel(
        string fullPath,
        string category,
        string dimensions,
        string size,
        string relevantDate,
        string confidence,
        string uncertainty,
        string transformations,
        string reason)
    {
        FullPath = fullPath;
        Category = category;
        Dimensions = dimensions;
        Size = size;
        RelevantDate = relevantDate;
        Confidence = confidence;
        Uncertainty = uncertainty;
        Transformations = transformations;
        Reason = reason;
    }

    public string FullPath { get; }
    public string Category { get; }
    public string Dimensions { get; }
    public string Size { get; }
    public string RelevantDate { get; }
    public string Confidence { get; }
    public string Uncertainty { get; }
    public string Transformations { get; }
    public string Reason { get; }
    public string Recommendation { get; } = "No recommendation; manual Selection.";
}
