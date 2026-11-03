using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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

    // Igal, 2026-10-25: a zoomed photo shows both scroll bars inside its viewer, so every part of
    // it can be reached; at 1 it fits with none. The two viewers pan together both ways.
    [Fact]
    public void AZoomedPhotoScrollsBothWaysInsideItsViewerAndTheOtherFollows()
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
                var leftControl = Named<PreviewStatusControl>(window, "LeftPreviewStatusControl");
                var left = leftControl.PreviewImageScroller;
                var right = Named<PreviewStatusControl>(window, "RightPreviewStatusControl").PreviewImageScroller;
                var zoom = Named<Slider>(window, "ReviewZoomSlider");
                Assert.Equal(Visibility.Collapsed, left.ComputedHorizontalScrollBarVisibility);
                Assert.Equal(Visibility.Collapsed, left.ComputedVerticalScrollBarVisibility);

                zoom.Value = 3;
                ShowAndDrain(window);

                foreach (var scroller in new[] { left, right })
                {
                    Assert.Equal(Visibility.Visible, scroller.ComputedHorizontalScrollBarVisibility);
                    Assert.Equal(Visibility.Visible, scroller.ComputedVerticalScrollBarVisibility);
                    Assert.True(scroller.ScrollableWidth > 0 && scroller.ScrollableHeight > 0,
                        $"The zoomed photo did not scroll both ways: {scroller.ScrollableWidth} by {scroller.ScrollableHeight}.");
                    // The box keeps the height a photo fits in; the photo scrolls inside it.
                    Assert.True(scroller.ActualHeight <= leftControl.FitHeight + 0.5,
                        $"The viewer grew to {scroller.ActualHeight} past its {leftControl.FitHeight}.");
                }

                left.ScrollToVerticalOffset(left.ScrollableHeight / 2);
                ShowAndDrain(window);
                Assert.Equal(right.ScrollableHeight / 2, right.VerticalOffset, 1.0);

                right.ScrollToVerticalOffset(right.ScrollableHeight);
                ShowAndDrain(window);
                Assert.Equal(left.ScrollableHeight, left.VerticalOffset, 1.0);

                zoom.Value = 1;
                ShowAndDrain(window);
                Assert.Equal(Visibility.Collapsed, left.ComputedVerticalScrollBarVisibility);
                Assert.Equal(0, left.ScrollableHeight, 0.5);
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

    // Igal, 2026-10-25: while a photo is dragged, a small picture of it follows the pointer: the
    // tile's own thumbnail, or the photo a viewer shows. It takes no hit test, so the viewer under
    // the pointer still gets the drop. DoDragDrop itself cannot run under the test harness, so the
    // picture's start and end are driven directly, and each move is the drag event WPF raises over
    // the element under the pointer. The window's root allows drops so that every element gets
    // one; the picture follows all of them, a viewer still takes its own drop, and a drag that no
    // viewer takes is refused, so a drop anywhere else does nothing.
    [Fact]
    public void ADraggedPhotoShowsItsOwnPictureAtThePointer()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var scope = new ReviewStaticScope();
            var workflow = new RecordingReviewWorkflow(scope.Current, ReviewData.CreateSnapshot());
            var viewModel = new ShellViewModel(scope, workflow, new RecordingReviewDialogService(),
                reviewPreviewSource: new SizedPreviewSource(40, 30),
                reviewThumbnailSource: new SizedThumbnailSource(new Dictionary<long, (int, int)>
                {
                    [1] = (4000, 3000), [2] = (4000, 3000), [3] = (4000, 3000), [4] = (4000, 3000),
                }));
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var window = ReviewTiles.Show(viewModel);
            try
            {
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var layer = Assert.IsType<System.Windows.Documents.AdornerLayer>(System.Windows.Documents.AdornerLayer.GetAdornerLayer(root));
                var tile = ReviewTiles.Tile(window, 1);
                var thumbnail = Assert.Single(WpfReviewSelectionTests.Descendants<PhotoThumbnail>(tile));
                WpfActionAccessibilityTests.PumpUntil(() => thumbnail.Image is not null);

                var picture = Assert.IsType<ReviewDragImage>(window.ShowDragImage(tile));

                Assert.Same(thumbnail.Image, picture.Picture);
                Assert.False(picture.IsHitTestVisible);
                Assert.Contains(picture, layer.GetAdorners(root) ?? []);
                Assert.InRange(Math.Max(picture.PictureSize.Width, picture.PictureSize.Height), 1, ReviewDragImage.Edge + 0.5);

                // Over the gallery the picture follows the pointer, and the drag is refused there.
                var group = viewModel.ReviewCategories[0].Groups[0];
                var fromTile = ReviewDragData.Create(1, group.GroupId);
                Assert.True(root.AllowDrop);
                Assert.True(tile.AllowDrop);
                var overTile = RaiseDrag(tile, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent,
                    fromTile, DragDropEffects.Copy, new Point(5, 6));
                window.UpdateLayout();
                AssertAtPointer(picture, tile.TranslatePoint(new Point(5, 6), root));
                Assert.Equal(DragDropEffects.None, overTile.Effects);

                // Entering a viewer, and moving over it, the viewer takes the photo and the picture
                // still follows; nothing is placed until the drop.
                var rightViewer = Named<UIElement>(window, "RightViewerPanel");
                var intoViewer = RaiseDrag(rightViewer, DragDrop.PreviewDragEnterEvent, DragDrop.DragEnterEvent,
                    fromTile, DragDropEffects.Copy, new Point(20, 30));
                window.UpdateLayout();
                AssertAtPointer(picture, rightViewer.TranslatePoint(new Point(20, 30), root));
                Assert.Equal(DragDropEffects.Copy, intoViewer.Effects);
                var overViewer = RaiseDrag(rightViewer, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent,
                    fromTile, DragDropEffects.Copy, new Point(40, 35));
                window.UpdateLayout();
                AssertAtPointer(picture, rightViewer.TranslatePoint(new Point(40, 35), root));
                Assert.Equal(DragDropEffects.Copy, overViewer.Effects);
                Assert.False(viewModel.RightViewer.HasPhoto);

                window.HideDragImage();
                Assert.Null(layer.GetAdorners(root));

                // A viewer's photo is dragged with the photo that viewer shows.
                Place(viewModel, ViewerSide.Left, group, 1);
                ShowAndDrain(window);
                var shown = Named<PreviewStatusControl>(window, "LeftPreviewStatusControl").PreviewImage.Source;
                Assert.NotNull(shown);
                var dragged = Assert.IsType<ReviewDragImage>(window.ShowDragImage(Named<UIElement>(window, "LeftViewerPanel")));
                Assert.Same(shown, dragged.Picture);

                // Dropped on the gallery, nothing takes it: the viewer lets it go only when the drag
                // ends (FinishViewerDrag), and nothing else changes.
                var dropOnGallery = RaiseDrag(tile, DragDrop.PreviewDropEvent, DragDrop.DropEvent,
                    ReviewDragData.Create(2, group.GroupId, ViewerSide.Left), DragDropEffects.Move, new Point(5, 6));
                Assert.Equal(DragDropEffects.None, dropOnGallery.Effects);

                window.HideDragImage();
                Assert.Null(layer.GetAdorners(root));
                Assert.Equal(2, viewModel.LeftViewer.Member?.OccurrenceId);
                Assert.False(viewModel.RightViewer.HasPhoto);
                Assert.Empty(workflow.SelectionCommands);
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

    // 1.2: in the Golden VM, which draws in software, the app closed with an access violation in
    // wpfgfx_cor3.dll (WPF's native renderer) while a photo was zoomed in and moved in the Right
    // Viewer. Each of 100 rounds takes two full-size photos (the viewer decodes up to 4,096 px)
    // through every zoom step up and down, to every edge at each step, and drags each viewer's
    // photo across the window, one onto the other viewer and one out, with each step drawn in
    // software before the next. It runs in a child process, so a renderer crash fails this test
    // instead of ending the run, and its last output lines say the round, zoom and action it
    // stopped at. WPF draws to the screen only on a signed-in desktop, which the Golden VM stages
    // do not have, so they leave it out and Invoke-AlbumenUiSmoke.ps1 -DesktopTest runs it there.
    [Fact]
    [Trait("Category", "Desktop")]
    public Task ZoomingScrollingAndDraggingFullSizePhotosNeverCrashesTheRenderer()
    {
        using (var current = Process.GetCurrentProcess())
            Assert.True((uint)current.SessionId == WTSGetActiveConsoleSessionId(),
                "This test draws on the signed-in console desktop; run it with Invoke-AlbumenUiSmoke.ps1 -DesktopTest.");
        return WpfScenarioIsolation.RunAsync(typeof(WpfReviewViewerTests), "viewer-stress",
            nameof(ZoomingScrollingAndDraggingFullSizePhotosNeverCrashesTheRenderer), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                // The run sends no input, so without this the guest would turn its display off and
                // WPF would stop presenting partway through. It lasts while this thread runs.
                Assert.NotEqual(0u, SetThreadExecutionState(KeepDisplayOn));
                var scope = new ReviewStaticScope();
                var workflow = new RecordingReviewWorkflow(scope.Current, ReviewData.CreateSnapshot());
                var viewModel = new ShellViewModel(scope, workflow, new RecordingReviewDialogService(),
                    reviewPreviewSource: new FullSizePreviewSource());
                WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
                var group = viewModel.ReviewCategories[0].Groups[0];
                Place(viewModel, ViewerSide.Left, group, 0);
                Place(viewModel, ViewerSide.Right, group, 1);
                var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
                using var progress = new StreamWriter(Console.OpenStandardOutput(), leaveOpen: true) { AutoFlush = true };
                try
                {
                    ShowAndDrain(window);
                    Assert.IsType<System.Windows.Interop.HwndSource>(PresentationSource.FromVisual(window))
                        .CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    var viewers = Named<FrameworkElement>(window, "ReviewViewersPanel");
                    viewers.BringIntoView();
                    var zoom = Named<Slider>(window, "ReviewZoomSlider");
                    var gallery = Named<UIElement>(window, "ReviewGalleryList");
                    var panels = new Dictionary<ViewerSide, FrameworkElement>
                    {
                        [ViewerSide.Left] = Named<FrameworkElement>(window, "LeftViewerPanel"),
                        [ViewerSide.Right] = Named<FrameworkElement>(window, "RightViewerPanel"),
                    };
                    var scrollers = new Dictionary<ViewerSide, ScrollViewer>
                    {
                        [ViewerSide.Left] = Named<PreviewStatusControl>(window, "LeftPreviewStatusControl").PreviewImageScroller,
                        [ViewerSide.Right] = Named<PreviewStatusControl>(window, "RightPreviewStatusControl").PreviewImageScroller,
                    };
                    Render(window);
                    var photo = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(
                        Named<PreviewStatusControl>(window, "LeftPreviewStatusControl").PreviewImage.Source);
                    Assert.Equal(4096, photo.PixelWidth);

                    // Every slider step up to the largest zoom and back down.
                    var up = Enumerable.Range(0, (int)Math.Round((zoom.Maximum - zoom.Minimum) / zoom.SmallChange) + 1)
                        .Select(step => zoom.Minimum + step * zoom.SmallChange).ToArray();
                    var steps = up.Concat(up.Reverse().Skip(1)).ToArray();
                    var clock = Stopwatch.StartNew();
                    for (var round = 1; round <= StressRounds; round++)
                    {
                        var leader = round % 2 == 1 ? ViewerSide.Left : ViewerSide.Right;
                        foreach (var step in steps)
                        {
                            Act(round, $"zoom {step:0.00}", () => zoom.Value = step);
                            // The far corner and back reach every edge from both sides; the other viewer follows.
                            foreach (var edge in StressEdges)
                                Act(round, $"scroll {leader} to {edge}", () =>
                                {
                                    scrollers[leader].ScrollToHorizontalOffset(edge * scrollers[leader].ScrollableWidth);
                                    scrollers[leader].ScrollToVerticalOffset(edge * scrollers[leader].ScrollableHeight);
                                });
                        }
                        // The drags take a different zoom step each round.
                        var dragZoom = up[round % up.Length];
                        Act(round, $"zoom {dragZoom:0.00}", () => zoom.Value = dragZoom);
                        var other = Other(leader);
                        Act(round, $"drag {leader} onto the other viewer", () => DragViewerPhoto(leader, ontoOther: true));
                        Act(round, $"drag {other} out", () => DragViewerPhoto(other, ontoOther: false));
                        progress.WriteLine($"ALBUMEN_VIEWER_STRESS round={round} done after {clock.Elapsed.TotalSeconds:0} s");
                    }

                    void Act(int round, string action, Action change)
                    {
                        progress.WriteLine($"ALBUMEN_VIEWER_STRESS round={round} zoom={zoom.Value:0.00} {action}");
                        change();
                        Render(window);
                    }

                    // What a drag of a viewer's photo does between the press and the release: the
                    // picture of the full-size photo follows the pointer over the viewers and the
                    // gallery, and a pan meanwhile moves both viewers. Dropped on the other viewer
                    // the photos swap; dropped outside the photo leaves its viewer and is put back.
                    void DragViewerPhoto(ViewerSide side, bool ontoOther)
                    {
                        var other = Other(side);
                        var dragged = Shown(side);
                        var stays = Shown(other);
                        var data = ReviewDragData.Create(dragged.OccurrenceId, group.GroupId, side);
                        Assert.NotNull(window.ShowDragImage(panels[side]));
                        Render(window);
                        foreach (var (target, x, y) in new (FrameworkElement, double, double)[]
                        {
                            (panels[side], 0.2, 0.3), (panels[side], 0.5, 0.5), (panels[side], 0.9, 0.9),
                            (panels[other], 0.1, 0.5), (panels[other], 0.6, 0.4),
                        })
                        {
                            RaiseDrag(target, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent, data,
                                DragDropEffects.Move, new Point(target.ActualWidth * x, target.ActualHeight * y));
                            Render(window);
                        }
                        RaiseDrag(gallery, DragDrop.PreviewDragOverEvent, DragDrop.DragOverEvent, data,
                            DragDropEffects.Move, new Point(20, 20));
                        scrollers[side].ScrollToHorizontalOffset(scrollers[side].ScrollableWidth / 2);
                        scrollers[side].ScrollToVerticalOffset(scrollers[side].ScrollableHeight / 2);
                        Render(window);
                        RaiseDrag(ontoOther ? panels[other] : gallery, DragDrop.PreviewDropEvent, DragDrop.DropEvent,
                            data, DragDropEffects.Move, new Point(30, 30));
                        window.HideDragImage();
                        window.FinishViewerDrag(side, droppedOnViewer: ontoOther, canceled: false);
                        if (ontoOther)
                        {
                            Assert.Equal(dragged.OccurrenceId, Shown(other).OccurrenceId);
                            Assert.Equal(stays.OccurrenceId, Shown(side).OccurrenceId);
                            return;
                        }
                        Assert.False(Slot(side).HasPhoto);
                        viewModel.PlaceInViewerCommand.ExecuteAsync(new PlaceInViewer(side, group.GroupId, dragged))
                            .GetAwaiter().GetResult();
                        Assert.Equal(dragged.OccurrenceId, Shown(side).OccurrenceId);
                        // The emptied viewer was shorter for a moment; the page keeps both in view.
                        window.UpdateLayout();
                        viewers.BringIntoView();
                    }

                    ReviewViewerSlot Slot(ViewerSide side) => side == ViewerSide.Left ? viewModel.LeftViewer : viewModel.RightViewer;
                    ScanOccurrenceViewModel Shown(ViewerSide side) => Assert.IsType<ScanOccurrenceViewModel>(Slot(side).Member);
                    static ViewerSide Other(ViewerSide side) => side == ViewerSide.Left ? ViewerSide.Right : ViewerSide.Left;
                }
                finally
                {
                    window.Close();
                }
                stage.CleanupSucceeded();
            }, StressLimit - TimeSpan.FromMinutes(1));
        }, StressLimit);
    }

    private const int StressRounds = 100;
    // The far corner, then back to the near one.
    private static readonly double[] StressEdges = [1.0, 0.0];

    // Not measured yet: the limit leaves the VM's software renderer room.
    private static readonly TimeSpan StressLimit = TimeSpan.FromMinutes(25);

    // ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED
    private const uint KeepDisplayOn = 0x80000003;

#pragma warning disable SYSLIB1054 // Same test-only import convention as the Infrastructure tests; tests do not enable unsafe code.
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
#pragma warning restore SYSLIB1054

    // WPF's internal MediaContext.From and CompleteRender, which draw and present a frame.
    private static readonly Type? MediaContext = typeof(System.Windows.Media.Visual).Assembly
        .GetType("System.Windows.Media.MediaContext");
    private static readonly MethodInfo? MediaContextFrom = MediaContext?.GetMethod(
        "From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(System.Windows.Threading.Dispatcher)]);
    private static readonly MethodInfo? CompleteRender = MediaContext?.GetMethod(
        "CompleteRender", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);

    // Lays the window out, sends the frame to WPF's renderer and waits until the renderer has drawn
    // and presented it, so each step is drawn before the next one changes it.
    private static void Render(Window window)
    {
        Assert.NotNull(MediaContextFrom);
        Assert.NotNull(CompleteRender);
        window.UpdateLayout();
        WpfReviewSelectionTests.Drain();
        CompleteRender.Invoke(MediaContextFrom.Invoke(null, [window.Dispatcher]), null);
    }

    // WPF makes a drag's event arguments only inside DoDragDrop. This raises what its drop target
    // raises over the element under the pointer: the preview event, then, only if no handler took
    // the preview, the bubbling one, with the same arguments; the effects are what the drag shows.
    private static DragEventArgs RaiseDrag(UIElement target, RoutedEvent preview, RoutedEvent bubble,
        IDataObject data, DragDropEffects allowed, Point pointInTarget)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)],
            modifiers: null);
        Assert.NotNull(constructor);
        var drag = Assert.IsType<DragEventArgs>(constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, allowed, target, pointInTarget]));
        drag.RoutedEvent = preview;
        target.RaiseEvent(drag);
        if (drag.Handled) return drag;
        drag.RoutedEvent = bubble;
        target.RaiseEvent(drag);
        return drag;
    }

    private static void AssertAtPointer(ReviewDragImage picture, Point pointerInRoot)
    {
        Assert.Equal(pointerInRoot.X + ReviewDragImage.Offset.X, picture.Position.X, 1.0);
        Assert.Equal(pointerInRoot.Y + ReviewDragImage.Offset.Y, picture.Position.Y, 1.0);
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

// Answers every viewer request at once with a photo as large as the viewer decodes one (4,096 px
// on the long edge): odd photos landscape (6000 x 4000), even ones portrait, with detail in every
// pixel so that no part of the picture is uniform.
internal sealed class FullSizePreviewSource : IReviewPreviewSource
{
    private static readonly Lazy<byte[]> Landscape = new(() => Pattern(4096, 2731));
    private static readonly Lazy<byte[]> Portrait = new(() => Pattern(2731, 4096));

    public Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        var landscape = occurrence.Token.OccurrenceId % 2 == 1;
        var pattern = landscape ? Landscape.Value : Portrait.Value;
        var pixels = MemoryPool<byte>.Shared.Rent(pattern.Length);
        pattern.CopyTo(pixels.Memory.Span);
        var frame = landscape
            ? new PreviewFrame(4096, 2731, 6000, 4000, pixels)
            : new PreviewFrame(2731, 4096, 4000, 6000, pixels);
        return Task.FromResult(ReviewPreviewResult.Success(occurrence.ResolvedPath, frame, 8_000_000, null));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static byte[] Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)(x ^ y);
                pixels[offset + 3] = 0xFF;
            }
        return pixels;
    }
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
