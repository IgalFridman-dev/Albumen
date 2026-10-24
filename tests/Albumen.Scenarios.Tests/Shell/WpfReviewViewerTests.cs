using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using Albumen.App;
using Albumen.App.Shell;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfReviewViewerTests
{
    [Fact]
    public void TheRightViewerAddsASecondPreviewThatSharesTheZoomAndClearingRemovesIt()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var group = viewModel.ReviewCategories[0].Groups[0];
            Place(viewModel, ViewerSide.Left, group, 0);
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                Assert.Single(WpfReviewSelectionTests.Descendants<PreviewStatusControl>(window));

                Place(viewModel, ViewerSide.Right, group, 1);
                ShowAndDrain(window);
                var previews = WpfReviewSelectionTests.Descendants<PreviewStatusControl>(window).ToArray();
                Assert.Equal(2, previews.Length);
                Assert.All(previews, preview => Assert.True(preview.IsVisible));

                Assert.IsType<Slider>(window.FindName("ReviewZoomSlider")).Value = 2;
                ShowAndDrain(window);
                Assert.All(previews, preview => Assert.Equal(2.0, preview.Zoom));
                Assert.Equal(previews[0].FitHeight, previews[1].FitHeight);

                viewModel.ClearViewerCommand.ExecuteAsync(ViewerSide.Right).GetAwaiter().GetResult();
                ShowAndDrain(window);
                Assert.Single(WpfReviewSelectionTests.Descendants<PreviewStatusControl>(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ViewedPhotosPanTogetherByTheSameShareOfTheirWidth()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
            var viewModel = new ShellViewModel(workflow.Scope, workflow,
                reviewPreviewSource: new SizedPreviewSource(40, 30));
            var near = viewModel.ReviewCategories.Single(row => row.Category == ReviewCategoryKind.Near);
            viewModel.OpenReviewCategoryCommand.ExecuteAsync(near).GetAwaiter().GetResult();
            var group = near.Groups[0];
            Place(viewModel, ViewerSide.Left, group, 0);
            Place(viewModel, ViewerSide.Right, group, 1);
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                Assert.IsType<Slider>(window.FindName("ReviewZoomSlider")).Value = 3;
                ShowAndDrain(window);
                var previews = WpfReviewSelectionTests.Descendants<PreviewStatusControl>(window).ToArray();
                Assert.Equal(2, previews.Length);
                var main = previews[0].PreviewImageScroller;
                var compared = previews[1].PreviewImageScroller;
                Assert.True(main.ScrollableWidth > 0 && compared.ScrollableWidth > 0,
                    $"Zoomed photos did not pan: {main.ScrollableWidth} and {compared.ScrollableWidth}.");

                main.ScrollToHorizontalOffset(main.ScrollableWidth / 2);
                ShowAndDrain(window);
                Assert.Equal(compared.ScrollableWidth / 2, compared.HorizontalOffset, 1.0);

                compared.ScrollToHorizontalOffset(compared.ScrollableWidth);
                ShowAndDrain(window);
                Assert.Equal(main.ScrollableWidth, main.HorizontalOffset, 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectionBarStaysOutsideThePageAndLeadsToTheApproval()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 960, Height = 720 };
            try
            {
                ShowAndDrain(window);
                var page = Assert.IsType<ScrollViewer>(window.FindName("ReviewPageScrollViewer"));
                var bar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ReviewSelectionBar"));
                Assert.True(bar.IsVisible);
                Assert.False(IsWithin(bar, page));
                Assert.Equal(fixture.ViewModel.ReviewSelectionBarText,
                    Assert.IsType<TextBlock>(window.FindName("ReviewSelectionBarText")).Text);

                var button = Assert.IsType<Button>(window.FindName("ReviewCleanupButton"));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ShowAndDrain(window);

                var approval = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ApprovalPanel"));
                Assert.True(approval.IsKeyboardFocused);
                var bounds = approval.TransformToAncestor(page).TransformBounds(
                    new Rect(new Point(), approval.RenderSize));
                Assert.True(bounds.Top >= -0.5 && bounds.Top < page.ViewportHeight,
                    $"Approval bounds {bounds} were outside the Review viewport height {page.ViewportHeight}.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Place(ShellViewModel viewModel, ViewerSide side, ReviewGroupViewModel group, int index) =>
        viewModel.PlaceInViewerCommand.ExecuteAsync(new PlaceInViewer(side, group.GroupId, group.Members[index]))
            .GetAwaiter().GetResult();

    private static void ShowAndDrain(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.Activate();
        window.UpdateLayout();
        WpfReviewSelectionTests.Drain();
        window.UpdateLayout();
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}

// Answers every thumbnail request at once with a tiny frame that reports the given photo size.
internal sealed class SizedThumbnailSource(IReadOnlyDictionary<long, (int Width, int Height)> sizes)
    : IReviewPreviewSource
{
    public int Requests { get; private set; }

    public Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        Requests++;
        var (width, height) = sizes[occurrence.Token.OccurrenceId];
        var frame = new PreviewFrame(1, 1, width, height, MemoryPool<byte>.Shared.Rent(4));
        return Task.FromResult(ReviewPreviewResult.Success(occurrence.ResolvedPath, frame, 512, null));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Answers every viewer request at once with a small solid frame that the viewer scales up.
internal sealed class SizedPreviewSource(int width, int height) : IReviewPreviewSource
{
    public Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        var pixels = MemoryPool<byte>.Shared.Rent(width * height * 4);
        pixels.Memory.Span.Fill(0x80);
        var frame = new PreviewFrame(width, height, width, height, pixels);
        return Task.FromResult(ReviewPreviewResult.Success(occurrence.ResolvedPath, frame, 512, null));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
