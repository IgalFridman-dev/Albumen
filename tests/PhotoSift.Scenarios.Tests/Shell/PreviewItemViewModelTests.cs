using PhotoSift.App.Shell;
using PhotoSift.Infrastructure.Windows.Preview;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class PreviewItemViewModelTests
{
    [Fact]
    public void FailurePresentationKeepsUnicodeLongPathCopyableAndAnnouncesLiteralReason()
    {
        var path = @"C:\very long folder\תמונות משפחה\旅行\" + new string('x', 260) + "\broken.jpg";
        using var result = PreviewDecodeResult.Failure(path, PreviewAvailability.CouldNotAnalyze,
            "Preview unavailable: the image could not be analyzed.");

        var item = new PreviewItemViewModel(result);

        Assert.Equal(path, item.FullPath);
        Assert.Equal("Preview unavailable: the image could not be analyzed.", item.StatusText);
        Assert.Equal($"{path}. Preview unavailable: the image could not be analyzed.",
            item.AutomationName);
    }
}
