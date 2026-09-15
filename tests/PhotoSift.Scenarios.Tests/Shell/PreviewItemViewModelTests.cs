using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Infrastructure.Windows.Preview;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class PreviewItemViewModelTests
{
    [Fact]
    public void FailurePresentationKeepsUnicodeLongPathCopyableAndAnnouncesLiteralReason()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var path = @"C:\very long folder\תמונות משפחה\旅行\" +
                new string('x', 260) + "\broken.jpg";
            using var result = PreviewDecodeResult.Failure(path,
                PreviewAvailability.CouldNotAnalyze,
                "Preview unavailable: the image could not be analyzed.");
            var item = new PreviewItemViewModel(result);
            var control = new PreviewStatusControl { DataContext = item };
            var window = new Window { Content = control };
            try
            {
                window.Show();
                window.Activate();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();

                var pathBox = Assert.IsType<TextBox>(
                    control.FindName("PreviewFullPathTextBox"));
                var status = Assert.IsType<TextBlock>(
                    control.FindName("PreviewStatusText"));
                Assert.Equal(path, pathBox.Text);
                Assert.True(pathBox.IsReadOnly);
                Assert.Equal(TextWrapping.Wrap, pathBox.TextWrapping);
                pathBox.Focus();
                pathBox.SelectAll();
                Assert.True(ApplicationCommands.Copy.CanExecute(null, pathBox));
                ApplicationCommands.Copy.Execute(null, pathBox);
                Assert.Equal(path, Clipboard.GetText());

                var peer = UIElementAutomationPeer.FromElement(control) ??
                    UIElementAutomationPeer.CreatePeerForElement(control);
                Assert.NotNull(peer);
                Assert.Equal(item.AutomationName, peer.GetName());
                Assert.Equal(item.StatusText, AutomationProperties.GetItemStatus(control));
                Assert.Equal(AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(status));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
