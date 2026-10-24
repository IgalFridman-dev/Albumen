using System.Buffers;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Albumen.App;
using Albumen.App.Review;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// 1.1.5: the Left and Right Viewers below the gallery.
[Collection("WPF UI Automation")]
public sealed class WpfReviewViewerTests
{
    [Fact]
    public void PlacingShowsThePhotoInThatViewerAndBothShareTheZoom()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var group = viewModel.ReviewCategories[0].Groups[0];
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                var left = Named<PreviewStatusControl>(window, "LeftPreviewStatusControl");
                var right = Named<PreviewStatusControl>(window, "RightPreviewStatusControl");
                Assert.False(left.IsVisible);
                Assert.False(right.IsVisible);

                Place(viewModel, ViewerSide.Left, group, 0);
                ShowAndDrain(window);
                Assert.True(left.IsVisible);
                Assert.False(right.IsVisible);
                Assert.Same(viewModel.LeftViewer.Preview, left.DataContext);

                Place(viewModel, ViewerSide.Right, group, 1);
                ShowAndDrain(window);
                Assert.True(right.IsVisible);
                Assert.Same(viewModel.RightViewer.Preview, right.DataContext);
                Assert.Equal(group.Members[1].FileName, viewModel.RightViewer.Member?.FileName);

                Named<Slider>(window, "ReviewZoomSlider").Value = 2;
                ShowAndDrain(window);
                Assert.Equal(2.0, left.Zoom);
                Assert.Equal(2.0, right.Zoom);
                Assert.Equal(left.FitHeight, right.FitHeight);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ViewersPanTogetherByTheSameShareOfTheirWidth()
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
                Named<Slider>(window, "ReviewZoomSlider").Value = 3;
                ShowAndDrain(window);
                var left = Named<PreviewStatusControl>(window, "LeftPreviewStatusControl").PreviewImageScroller;
                var right = Named<PreviewStatusControl>(window, "RightPreviewStatusControl").PreviewImageScroller;
                Assert.True(left.ScrollableWidth > 0 && right.ScrollableWidth > 0,
                    $"Zoomed photos did not pan: {left.ScrollableWidth} and {right.ScrollableWidth}.");

                left.ScrollToHorizontalOffset(left.ScrollableWidth / 2);
                ShowAndDrain(window);
                Assert.Equal(right.ScrollableWidth / 2, right.HorizontalOffset, 1.0);

                right.ScrollToHorizontalOffset(right.ScrollableWidth);
                ShowAndDrain(window);
                Assert.Equal(left.ScrollableWidth, left.HorizontalOffset, 1.0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AnEmptyViewerSaysAPhotoCanBeDraggedIntoIt()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                foreach (var (panelName, buttonName, title) in new[]
                {
                    ("LeftViewerPanel", "LeftViewerRemoveButton", "Left Viewer"),
                    ("RightViewerPanel", "RightViewerRemoveButton", "Right Viewer"),
                })
                {
                    var panel = Named<Border>(window, panelName);
                    Assert.True(panel.AllowDrop);
                    Assert.Equal("review-viewer", AutomationProperties.GetAutomationId(panel));
                    Assert.Equal($"{title}: empty", AutomationProperties.GetName(panel));
                    Assert.Equal(ReviewViewerSlot.EmptyText, AutomationProperties.GetItemStatus(panel));
                    Assert.Contains(WpfReviewSelectionTests.Descendants<TextBlock>(panel),
                        text => text.IsVisible && text.Text == "You can drag a photo to this box to view it in detail");
                    Assert.False(Named<Button>(window, buttonName).IsVisible);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DroppingTheDragDataPlacesThePhoto()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var group = viewModel.ReviewCategories[0].Groups[0];
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);

                Assert.True(window.TryPlaceFromData(
                    ReviewDragData.Create(group.Members[3].OccurrenceId, group.GroupId), ViewerSide.Right));
                ShowAndDrain(window);

                Assert.Same(group.Members[3], viewModel.RightViewer.Member);
                Assert.False(viewModel.LeftViewer.HasPhoto);
                Assert.Single(fixture.Preview.Requests);
                Assert.True(Named<PreviewStatusControl>(window, "RightPreviewStatusControl").IsVisible);

                // Anything else dropped on a viewer places nothing.
                Assert.False(window.TryPlaceFromData(new DataObject(DataFormats.Text, "C:\\Photos\\1.jpg"),
                    ViewerSide.Left));
                Assert.False(viewModel.LeftViewer.HasPhoto);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DroppingAViewersPhotoOnTheOtherViewerSwapsThem()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var group = viewModel.ReviewCategories[0].Groups[0];
            Place(viewModel, ViewerSide.Left, group, 0);
            Place(viewModel, ViewerSide.Right, group, 3);
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);

                // Dropped back on its own viewer, the photo stays where it is.
                Assert.True(window.TryPlaceFromData(
                    ReviewDragData.Create(1, group.GroupId, ViewerSide.Left), ViewerSide.Left));
                Assert.Equal(1, viewModel.LeftViewer.Member?.OccurrenceId);

                Assert.True(window.TryPlaceFromData(
                    ReviewDragData.Create(1, group.GroupId, ViewerSide.Left), ViewerSide.Right));
                ShowAndDrain(window);

                Assert.Equal(4, viewModel.LeftViewer.Member?.OccurrenceId);
                Assert.Equal(1, viewModel.RightViewer.Member?.OccurrenceId);
                Assert.Equal(2, fixture.Preview.Requests.Count);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DraggingAViewersPhotoOutClearsOnlyThatViewer()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create(review: _ => ReviewData.CreateSnapshot(selected: [1]));
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var group = viewModel.ReviewCategories[0].Groups[0];
            Place(viewModel, ViewerSide.Left, group, 0);
            Place(viewModel, ViewerSide.Right, group, 3);
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);

                // Esc, or a drop on a viewer, leaves both viewers as they are.
                window.FinishViewerDrag(ViewerSide.Left, droppedOnViewer: false, canceled: true);
                window.FinishViewerDrag(ViewerSide.Left, droppedOnViewer: true, canceled: false);
                Assert.Equal(1, viewModel.LeftViewer.Member?.OccurrenceId);

                window.FinishViewerDrag(ViewerSide.Left, droppedOnViewer: false, canceled: false);
                ShowAndDrain(window);

                Assert.False(viewModel.LeftViewer.HasPhoto);
                Assert.Equal(4, viewModel.RightViewer.Member?.OccurrenceId);
                // Leaving the viewer is not unmarking: photo 1 is still marked in its set.
                Assert.True(viewModel.ReviewCategories[0].Groups[0].Members[0].IsSelected);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheRemoveButtonMarksThroughTheSelectionPathAndTurnsRed()
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
                var button = Named<Button>(window, "LeftViewerRemoveButton");
                Assert.True(button.IsVisible && button.IsEnabled);
                Assert.Equal("Remove this photo", button.Content);
                Assert.Equal("Remove the photo in the Left Viewer", AutomationProperties.GetName(button));
                Assert.Same(window.FindResource("Albumen.Button"), button.Style);
                Focus(button);

                Invoke(button);
                ShowAndDrain(window);

                var mark = Assert.Single(fixture.Workflow.SelectionCommands);
                Assert.Equal(SelectionCommandKind.Toggle, mark.Kind);
                Assert.Equal(group.GroupId, mark.Pin.GroupId);
                Assert.Equal([1L], mark.OccurrenceIds);
                Assert.Same(window.FindResource("Albumen.DangerButton"), button.Style);
                Assert.Equal("Unmark this photo", button.Content);
                Assert.Equal("Unmark the photo in the Left Viewer", AutomationProperties.GetName(button));
                Assert.True(button.IsEnabled);
                Assert.True(button.IsKeyboardFocused);

                Invoke(button);
                ShowAndDrain(window);

                Assert.Equal(2, fixture.Workflow.SelectionCommands.Count);
                Assert.Equal(SelectionCommandKind.Toggle, fixture.Workflow.SelectionCommands[1].Kind);
                Assert.Same(window.FindResource("Albumen.Button"), button.Style);
                Assert.Equal("Remove this photo", button.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheLastKeptPhotoAsksForTheOverrideFromTheViewerButton()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var dialog = new RecordingReviewDialogService { Result = SurvivorOverrideDialogResult.Cancel };
            var fixture = GroupedWpfFixture.Create(dialog);
            var before = fixture.Workflow.Current.Review;
            var proposal = ReviewData.Proposal(before);
            fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.OverrideRequired, before, proposal);
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            Place(viewModel, ViewerSide.Right, viewModel.ReviewCategories[0].Groups[0], 0);
            var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                var button = Named<Button>(window, "RightViewerRemoveButton");
                Focus(button);

                Invoke(button);
                ShowAndDrain(window);

                Assert.Equal([proposal], dialog.Proposals);
                Assert.Empty(fixture.Workflow.Affirmations);
                Assert.Same(before, fixture.Workflow.Current.Review);
                Assert.False(viewModel.RightViewer.IsMarked);
                Assert.Equal("Remove this photo", button.Content);
                Assert.True(button.IsKeyboardFocused);
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

    private static T Named<T>(MainWindow window, string name) where T : class =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void Focus(UIElement element)
    {
        Assert.True(element.Focus());
        Keyboard.Focus(element);
        Assert.True(element.IsKeyboardFocused);
    }

    // UI Automation's Invoke runs the button's click, with its command, as a user's click would.
    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        Assert.NotNull(peer);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        WpfReviewSelectionTests.Drain();
    }

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
