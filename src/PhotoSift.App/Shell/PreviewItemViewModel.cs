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

    public string FullPath { get; }
    public string StatusText { get; }
    public string AutomationName { get; }
}
