using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Albumen.App;
using Albumen.App.Review;
using Albumen.App.Shell;
using Albumen.Domain.Review;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// 1.1.5: every photo of a set is one tile that can be placed, marked and unmarked by mouse,
// keyboard and UI Automation, shows its state, and gives its facts on hover.
[Collection("WPF UI Automation")]
public sealed class WpfReviewTileTests
{
    [Fact]
    public void TheTileMenuOffersRemoveOrUnmarkAndBothPlacements()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var window = ReviewTiles.Show(viewModel);
            try
            {
                var tile = ReviewTiles.Tile(window, 1);
                var menu = Assert.IsType<ContextMenu>(tile.ContextMenu);
                Assert.Equal(["Remove", "Place in Left Viewer", "Place in Right Viewer"],
                    menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray());
                Assert.IsType<Separator>(menu.Items[1]);
                var remove = ReviewTiles.RemoveItem(tile);
                Assert.Equal("selection-control", AutomationProperties.GetAutomationId(remove));
                Assert.True(remove.IsEnabled);

                ReviewTiles.Click(ReviewTiles.PlaceItem(tile, ViewerSide.Right));
                Assert.Equal(1, viewModel.RightViewer.Member?.OccurrenceId);
                // A photo is in at most one viewer: placing it in the other one moves it.
                ReviewTiles.Click(ReviewTiles.PlaceItem(tile, ViewerSide.Left));
                Assert.Equal(1, viewModel.LeftViewer.Member?.OccurrenceId);
                Assert.False(viewModel.RightViewer.HasPhoto);
                Assert.Empty(fixture.Workflow.SelectionCommands);

                ReviewTiles.Click(remove);
                ReviewTiles.ShowAndDrain(window);

                var mark = Assert.Single(fixture.Workflow.SelectionCommands);
                Assert.Equal(SelectionCommandKind.Toggle, mark.Kind);
                Assert.Equal([1L], mark.OccurrenceIds);
                var marked = ReviewTiles.Tile(window, 1);
                Assert.Equal("Unmark for removal", ReviewTiles.RemoveItem(marked).Header);
                Assert.True(ReviewTiles.RemoveItem(marked).IsEnabled);
                // A photo still being checked again can't be marked from its menu.
                Assert.Equal("Remove", ReviewTiles.RemoveItem(ReviewTiles.Tile(window, 2)).Header);
                Assert.False(ReviewTiles.RemoveItem(ReviewTiles.Tile(window, 2)).IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AClickPlacesOnlyIntoAnEmptyLeftViewerAndSpaceNeverMarks()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var window = ReviewTiles.Show(viewModel);
            try
            {
                var first = ReviewTiles.Tile(window, 1);
                Assert.Equal("review-photo", AutomationProperties.GetAutomationId(first));
                ReviewTiles.Invoke(first);
                Assert.Equal(1, viewModel.LeftViewer.Member?.OccurrenceId);

                var fourth = ReviewTiles.Tile(window, 4);
                ReviewTiles.Invoke(fourth);
                ReviewTiles.Focus(fourth);
                WpfReviewSelectionTests.Press(fourth, Key.Space);
                WpfReviewSelectionTests.Press(fourth, Key.Enter);
                WpfReviewSelectionTests.Drain();

                Assert.Equal(1, viewModel.LeftViewer.Member?.OccurrenceId);
                Assert.False(viewModel.RightViewer.HasPhoto);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HoverFactsNameThePhotoItsPathSizeAndKind()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var scope = new ReviewStaticScope();
            var workflow = new RecordingReviewWorkflow(scope.Current, ReviewData.CreateSnapshot());
            var viewModel = new ShellViewModel(scope, workflow, new RecordingReviewDialogService(),
                reviewThumbnailSource: new SizedThumbnailSource(new Dictionary<long, (int, int)>
                {
                    [1] = (4000, 3000), [2] = (4000, 3000), [3] = (4000, 3000), [4] = (4000, 3000),
                }));
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var window = ReviewTiles.Show(viewModel);
            try
            {
                var tile = ReviewTiles.Tile(window, 1);
                var member = Assert.IsType<ScanOccurrenceViewModel>(tile.Member);
                var lines = ReviewTiles.Facts(tile, out var path);

                Assert.Contains(member.FileName, lines);
                Assert.Equal(member.DiscoveredPath, path.Text);
                Assert.True(path.IsReadOnly);
                Assert.False(path.IsTabStop);
                Assert.Contains("512 bytes", lines);
                Assert.Contains("4000 × 3000", lines);
                Assert.Contains("The files are identical, byte for byte.", lines);
                Assert.Contains(ScanOccurrenceViewModel.RecommendationText, lines);
                Assert.DoesNotContain(lines, line => line.StartsWith("Kept", StringComparison.Ordinal));

                // A photo that can't be marked says why.
                var pending = ReviewTiles.Facts(ReviewTiles.Tile(window, 2), out _);
                Assert.Contains("Being checked again, so it can't be marked yet", pending);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheShownViewerIsWrittenOnTheTile()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var viewModel = fixture.ViewModel;
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var group = viewModel.ReviewCategories[0].Groups[0];
            var window = ReviewTiles.Show(viewModel);
            try
            {
                viewModel.PlaceInViewerCommand.ExecuteAsync(new PlaceInViewer(ViewerSide.Left, group.GroupId, group.Members[0]))
                    .GetAwaiter().GetResult();
                viewModel.PlaceInViewerCommand.ExecuteAsync(new PlaceInViewer(ViewerSide.Right, group.GroupId, group.Members[2]))
                    .GetAwaiter().GetResult();
                ReviewTiles.ShowAndDrain(window);

                AssertShownIn(ReviewTiles.Tile(window, 1), "Left Viewer");
                AssertShownIn(ReviewTiles.Tile(window, 3), "Right Viewer");
                AssertShownIn(ReviewTiles.Tile(window, 2), string.Empty);
                // The photos of the other set are not touched.
                AssertShownIn(ReviewTiles.Tile(window, 11), string.Empty);

                viewModel.ClearViewerCommand.ExecuteAsync(ViewerSide.Left).GetAwaiter().GetResult();
                ReviewTiles.ShowAndDrain(window);
                AssertShownIn(ReviewTiles.Tile(window, 1), string.Empty);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ARemovedSignShowsOnAMarkedTile()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create(review: _ => ReviewData.CreateSnapshot(twoGroups: true, selected: [1]));
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var marked = ReviewTiles.Tile(window, 1);
                Assert.Contains(VisibleTexts(marked), text => text == "Removed");
                Assert.StartsWith("Marked for removal:", AutomationProperties.GetName(marked), StringComparison.Ordinal);
                Assert.Equal(ToggleState.On, ReviewTiles.ToggleStateOf(marked));

                var kept = ReviewTiles.Tile(window, 4);
                Assert.DoesNotContain(VisibleTexts(kept), text => text == "Removed");
                Assert.Equal(ToggleState.Off, ReviewTiles.ToggleStateOf(kept));
                // No Keep wording on any tile: only marked photos carry a sign.
                Assert.DoesNotContain(WpfReviewSelectionTests.Descendants<ReviewPhotoTile>(window).SelectMany(VisibleTexts),
                    text => text.Contains("Keep", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ARemoveFromTheMenuRestoresFocusToTheTileAfterTheProjection()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                ReviewTiles.Focus(Assert.IsType<TextBlock>(window.FindName("ReviewHeading")));
                var tile = ReviewTiles.Tile(window, 4);

                ReviewTiles.Click(ReviewTiles.RemoveItem(tile));
                ReviewTiles.ShowAndDrain(window);

                Assert.Single(fixture.Workflow.SelectionCommands);
                var replacement = ReviewTiles.Tile(window, 4);
                Assert.NotSame(tile, replacement);
                Assert.True(replacement.IsKeyboardFocused);
                Assert.Equal(ToggleState.On, ReviewTiles.ToggleStateOf(replacement));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SuggestedBestShowsOnItsTileWithoutSelectingAnything()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
            var viewModel = new ShellViewModel(workflow.Scope, workflow,
                reviewThumbnailSource: new SizedThumbnailSource(new Dictionary<long, (int, int)>
                {
                    [11] = (1600, 1200),
                    [12] = (4000, 3000),
                }));
            var near = viewModel.ReviewCategories.Single(row => row.Category == ReviewCategoryKind.Near);
            viewModel.OpenReviewCategoryCommand.ExecuteAsync(near).GetAwaiter().GetResult();
            var window = ReviewTiles.Show(viewModel);
            try
            {
                ReviewTiles.ShowAndDrain(window);

                Assert.Equal(new SuggestedBest(12, "most pixels"), near.Groups[0].Suggestion);
                var badges = WpfReviewSelectionTests.Descendants<TextBlock>(window)
                    .Where(text => text.IsVisible && text.Text.StartsWith("Suggested best:", StringComparison.Ordinal))
                    .ToArray();
                var badge = Assert.Single(badges);
                Assert.Equal(12L, Assert.IsType<ScanOccurrenceViewModel>(badge.DataContext).OccurrenceId);
                Assert.Contains("most pixels", badge.Text, StringComparison.Ordinal);
                Assert.Empty(workflow.SelectionCommands);
                Assert.All(near.Groups[0].Members, member => Assert.False(member.IsSelected));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // PM-022: a search's match in no open set is a tile that does something when clicked: it
    // opens read-only in a viewer, and its menu only places it.
    [Fact]
    public void AnOtherMatchTileHasNoToggleAndOnlyPlaceItems()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var search = fixture.SearchAsync("at sea");
            WpfActionAccessibilityTests.PumpUntil(() => search.IsCompleted);
            var other = fixture.ViewModel.ReviewCategories.Single(category => category.Category == ReviewCategoryKind.OtherMatches);
            fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(other).GetAwaiter().GetResult();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var tile = ReviewTiles.Tile(window, 50);
                var menu = Assert.IsType<ContextMenu>(tile.ContextMenu);
                Assert.Equal(["Place in Left Viewer", "Place in Right Viewer"],
                    menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray());
                Assert.Empty(menu.Items.OfType<Separator>());
                var peer = UIElementAutomationPeer.FromElement(tile) ?? UIElementAutomationPeer.CreatePeerForElement(tile);
                Assert.Null(peer.GetPattern(PatternInterface.Toggle));
                Assert.StartsWith(ScanOccurrenceViewModel.NotInAnySetText, AutomationProperties.GetItemStatus(tile),
                    StringComparison.Ordinal);
                Assert.Contains(VisibleTexts(tile), text => text == "Matches");

                ReviewTiles.Invoke(tile);
                ReviewTiles.ShowAndDrain(window);

                Assert.Equal(50, fixture.ViewModel.LeftViewer.Member?.OccurrenceId);
                Assert.False(Assert.IsType<Button>(window.FindName("LeftViewerRemoveButton")).IsVisible);
                Assert.Contains(VisibleTexts(Assert.IsAssignableFrom<DependencyObject>(window.FindName("LeftViewerPanel"))),
                    text => text == ScanOccurrenceViewModel.NotInAnySetText);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertShownIn(ReviewPhotoTile tile, string viewer)
    {
        Assert.Equal(viewer, tile.ShownIn);
        Assert.Equal(viewer.Length != 0, tile.IsShownInViewer);
        var thumbnail = Assert.Single(WpfReviewSelectionTests.Descendants<PhotoThumbnail>(tile).ToArray());
        Assert.Equal(viewer.Length != 0 ? 0.4 : 1.0, thumbnail.Opacity, 3);
        if (viewer.Length != 0) Assert.Contains(VisibleTexts(tile), text => text == viewer);
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root) =>
        WpfReviewSelectionTests.Descendants<TextBlock>(root).Where(text => text.IsVisible).Select(text => text.Text);
}

// Finding and driving gallery tiles in the Review WPF tests.
internal static class ReviewTiles
{
    internal static MainWindow Show(ShellViewModel viewModel, double width = 1280, double height = 800)
    {
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
        ShowAndDrain(window);
        return window;
    }

    internal static void ShowAndDrain(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.Activate();
        window.UpdateLayout();
        WpfReviewSelectionTests.Drain();
        window.UpdateLayout();
    }

    // The visible tile of a photo; the fixtures give every photo of the open kind its own id.
    internal static ReviewPhotoTile Tile(DependencyObject root, long occurrenceId) =>
        Assert.Single(WpfReviewSelectionTests.Descendants<ReviewPhotoTile>(root),
            tile => tile.IsVisible && tile.Member?.OccurrenceId == occurrenceId);

    internal static MenuItem RemoveItem(ReviewPhotoTile tile) =>
        Assert.Single(Items(tile), item => AutomationProperties.GetAutomationId(item) == "selection-control");

    internal static MenuItem PlaceItem(ReviewPhotoTile tile, ViewerSide side) =>
        Assert.Single(Items(tile), item => Equals(item.Header, $"Place in {side} Viewer"));

    // Opens the focused tile's menu as the Menu key or Shift+F10 would, with "Remove" focused,
    // so a key pressed on the returned item goes through the open menu.
    internal static MenuItem OpenMenuAtRemove(ReviewPhotoTile tile)
    {
        var menu = Assert.IsType<ContextMenu>(tile.ContextMenu);
        menu.PlacementTarget = tile;
        menu.IsOpen = true;
        WpfReviewSelectionTests.Drain();
        Assert.True(menu.IsOpen);
        var remove = RemoveItem(tile);
        Focus(remove);
        return remove;
    }

    // A menu item's click, as when it is chosen from the open menu.
    internal static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        WpfReviewSelectionTests.Drain();
    }

    internal static void Invoke(UIElement tile)
    {
        Assert.IsAssignableFrom<IInvokeProvider>(Peer(tile).GetPattern(PatternInterface.Invoke)).Invoke();
        WpfReviewSelectionTests.Drain();
    }

    internal static void Toggle(UIElement tile)
    {
        Assert.IsAssignableFrom<IToggleProvider>(Peer(tile).GetPattern(PatternInterface.Toggle)).Toggle();
        WpfReviewSelectionTests.Drain();
    }

    internal static ToggleState ToggleStateOf(UIElement tile) =>
        Assert.IsAssignableFrom<IToggleProvider>(Peer(tile).GetPattern(PatternInterface.Toggle)).ToggleState;

    internal static void Focus(UIElement element)
    {
        Assert.True(element.Focus());
        Keyboard.Focus(element);
        Assert.True(element.IsKeyboardFocused);
    }

    // Opens the tile's hover facts as the tooltip service would and reads their lines.
    internal static string[] Facts(ReviewPhotoTile tile, out TextBox path)
    {
        var facts = Assert.IsType<ToolTip>(tile.ToolTip);
        Assert.Same(tile.Member, facts.Content);
        facts.PlacementTarget = tile;
        facts.IsOpen = true;
        try
        {
            WpfReviewSelectionTests.Drain();
            facts.UpdateLayout();
            path = Assert.Single(WpfReviewSelectionTests.Descendants<TextBox>(facts).ToArray());
            return WpfReviewSelectionTests.Descendants<TextBlock>(facts)
                .Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        }
        finally
        {
            facts.IsOpen = false;
        }
    }

    private static MenuItem[] Items(ReviewPhotoTile tile) =>
        Assert.IsType<ContextMenu>(tile.ContextMenu).Items.OfType<MenuItem>().ToArray();

    private static AutomationPeer Peer(UIElement element) =>
        UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
}
