using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Albumen.App.Shell;
using Albumen.Application.Semantic;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// 1.1.5: the photo search is a short bar at the top centre, in the row of the Photos and History
// tabs: a magnifier, a placeholder that goes away on typing and a clear button, without the 1.1
// helper lines. It shows only in Review, on the Photos tab.
[Collection("WPF UI Automation")]
public sealed class WpfReviewSearchBarTests
{
    private const string Placeholder = "Find photos by what they show";

    [Fact]
    public void TheBarSitsInTheTabRowAndIsCentred()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel, width: 1280);
            try
            {
                var box = PromptBox(window);
                Assert.True(box.IsVisible);
                Assert.True(IsWithin(box, HeaderRow(window)));
                Assert.False(IsWithin(box, Assert.IsType<ScrollViewer>(window.FindName("ReviewPageScrollViewer"))));

                var bar = Element(window, "SearchBar");
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var centre = bar.TransformToAncestor(root).Transform(new Point(bar.ActualWidth / 2, 0)).X;
                Assert.InRange(centre, root.ActualWidth / 2 - 8, root.ActualWidth / 2 + 8);
                // One short row, narrower than the page: at most 320 px wide and 32 px tall.
                Assert.InRange(bar.ActualWidth, 200, 320);
                Assert.Equal(32, Element(window, "SearchField").ActualHeight, 0.5);

                var find = Assert.IsType<Button>(window.FindName("LikelyMatchesFindButton"));
                Assert.Equal("", find.Content);
                Assert.Equal("Find photos", AutomationProperties.GetName(find));
                Assert.Equal("Describe the photos to find", AutomationProperties.GetName(box));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ThePlaceholderDisappearsOnTypingAndReturnsOnClear()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var box = PromptBox(window);
                var placeholder = PlaceholderOf(window);
                var clear = Assert.IsType<Button>(window.FindName("LikelyMatchesShowAllButton"));
                Assert.True(placeholder.IsVisible);
                Assert.Equal(Placeholder, placeholder.Text);
                Assert.False(placeholder.IsHitTestVisible);
                Assert.False(clear.IsVisible);

                box.Text = "at sea";
                ReviewTiles.ShowAndDrain(window);
                Assert.Equal("at sea", fixture.Search.Prompt);
                Assert.False(placeholder.IsVisible);

                Search(fixture, window, box);
                Assert.True(fixture.Search.IsActive);
                Assert.True(clear.IsVisible);
                Assert.Equal("", clear.Content);
                Assert.Equal("Show all Review results", AutomationProperties.GetName(clear));

                ReviewTiles.Invoke(clear);
                ReviewTiles.ShowAndDrain(window);

                Assert.False(fixture.Search.IsActive);
                Assert.Equal(string.Empty, box.Text);
                Assert.True(placeholder.IsVisible);
                Assert.False(clear.IsVisible);
                // The prompt keeps focus, so the next search starts there.
                Assert.True(box.IsKeyboardFocusWithin);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EscapeClearsAndEnterSearches()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var box = PromptBox(window);
                box.Text = "at sea";

                Search(fixture, window, box);

                Assert.True(fixture.Search.IsActive);
                // One search is one ranking: the prompt is encoded once.
                Assert.Equal(["a photo of at sea"], fixture.Encoder.Prompts);

                WpfReviewSelectionTests.Press(box, Key.Escape);
                ReviewTiles.ShowAndDrain(window);

                Assert.False(fixture.Search.IsActive);
                Assert.Equal(string.Empty, fixture.Search.Prompt);
                Assert.Equal(string.Empty, box.Text);
                Assert.True(PlaceholderOf(window).IsVisible);
                Assert.Single(fixture.Encoder.Prompts);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheStatusLineIsHiddenWhileIdle()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var status = StatusLine(window);
                Assert.False(status.IsVisible);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                Assert.True(IsWithin(status, HeaderRow(window)));
                // The 1.1 helper lines are gone; the Scan's coverage is the bar's tooltip.
                Assert.DoesNotContain(VisibleTexts(window), text => text.StartsWith("Describe what", StringComparison.Ordinal)
                    || text.StartsWith("Searches only", StringComparison.Ordinal));
                Assert.Equal("Searches only the 6 photos in this Scan.", Element(window, "SearchField").ToolTip);

                var box = PromptBox(window);
                box.Text = "nothing";
                Search(fixture, window, box);

                Assert.True(status.IsVisible);
                Assert.Equal("No likely matches in this Scan for “nothing”. Try other words.", status.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheBarIsHiddenOutsideReviewAndOnHistory()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var bar = Element(window, "SearchBar");
                var tabs = WpfReviewSelectionTests.Descendants<TabControl>(window).Single().Items.Cast<TabItem>().ToArray();
                var photos = tabs.Single(item => Equals(item.Header, "Photos"));
                var history = tabs.Single(item => Equals(item.Header, "History"));
                Assert.True(bar.IsVisible);

                history.IsSelected = true;
                ReviewTiles.ShowAndDrain(window);
                Assert.False(bar.IsVisible);

                photos.IsSelected = true;
                ReviewTiles.ShowAndDrain(window);
                Assert.True(bar.IsVisible);

                fixture.Workflow.Publish(fixture.Workflow.Current with { CurrentPhase = WorkflowPhase.Scan });
                ReviewTiles.ShowAndDrain(window);
                Assert.False(bar.IsVisible);

                fixture.Workflow.Publish(fixture.Workflow.Current with { CurrentPhase = WorkflowPhase.Review });
                ReviewTiles.ShowAndDrain(window);
                Assert.True(bar.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WhenSearchCannotRunTheBarSaysWhyInItsPlaceholder()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture(index: new(SemanticState.Indexing, 6, 3, 3, SearchFixture.Profile.Fingerprint, ""));
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var box = PromptBox(window);
                var placeholder = PlaceholderOf(window);
                var find = Assert.IsType<Button>(window.FindName("LikelyMatchesFindButton"));
                Assert.True(placeholder.IsVisible);
                Assert.Equal("Getting photos ready for search: 3 of 6 ready.", placeholder.Text);
                Assert.False(box.IsEnabled);
                Assert.False(find.IsEnabled);

                // The bar enables itself once the photos are ready.
                fixture.Workflow.Publish(fixture.Workflow.Current with
                {
                    Scan = fixture.Workflow.Current.Scan with { Semantic = SearchFixture.Ready },
                });
                ReviewTiles.ShowAndDrain(window);

                Assert.Equal(Placeholder, placeholder.Text);
                Assert.True(box.IsEnabled);
                Assert.True(find.IsEnabled);
                Assert.False(StatusLine(window).IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AFilteredGalleryLabelsMatchesAndTheApprovalSaysWhatIsHidden()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var box = PromptBox(window);
                box.Text = "at sea";
                Search(fixture, window, box);

                // The identical-copies set shows whole; only its matching photo carries the label.
                Assert.Contains(VisibleTexts(ReviewTiles.Tile(window, 1)), text => text == "Matches");
                Assert.DoesNotContain(VisibleTexts(ReviewTiles.Tile(window, 2)), text => text == "Matches");
                Assert.Contains(VisibleTexts(window), text => text == "1 of 1 set");

                box.Text = "lone";
                Search(fixture, window, box);

                var hidden = Assert.IsType<TextBlock>(window.FindName("ApprovalHiddenMarkedText"));
                Assert.True(hidden.IsVisible);
                Assert.Equal("It is hidden by the search.", hidden.Text);
                Assert.EndsWith(" · 1 hidden by the search",
                    Assert.IsType<TextBlock>(window.FindName("ReviewSelectionBarText")).Text, StringComparison.Ordinal);

                box.Text = "nothing";
                Search(fixture, window, box);

                var empty = Assert.IsType<TextBlock>(window.FindName("ReviewSearchEmptyText"));
                Assert.True(empty.IsVisible);
                Assert.Equal("No photos match “nothing”", empty.Text);
                var clear = Assert.IsType<Button>(window.FindName("ReviewSearchClearButton"));
                Assert.Equal("Show all Review results", AutomationProperties.GetName(clear));

                ReviewTiles.Invoke(clear);
                ReviewTiles.ShowAndDrain(window);

                Assert.False(fixture.Search.IsActive);
                Assert.False(empty.IsVisible);
                Assert.False(hidden.IsVisible);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ASearchAfterAPhotoHadFocusKeepsFocusInTheBox()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                // A photo's tile held focus, so a projection would give focus back to it.
                ReviewTiles.Focus(ReviewTiles.Tile(window, 2));
                var box = PromptBox(window);
                box.Text = "at sea";

                Search(fixture, window, box);

                Assert.True(fixture.Search.IsActive);
                Assert.True(box.IsKeyboardFocused);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // The filter replaced the 1.1 strip of results: a search shows its matches in the kinds.
    [Fact]
    public void NoResultStripIsShownUnderTheBar()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var box = PromptBox(window);
                box.Text = "at sea";
                Search(fixture, window, box);

                Assert.True(fixture.Search.IsActive);
                Assert.Null(window.FindName("LikelyMatchesList"));
                Assert.DoesNotContain(WpfReviewSelectionTests.Descendants<Button>(window),
                    button => AutomationProperties.GetAutomationId(button) == "open-likely-match");
                Assert.DoesNotContain(WpfReviewSelectionTests.Descendants<ListBox>(window),
                    list => AutomationProperties.GetName(list) == "Likely matches in this Scan");
                Assert.DoesNotContain(VisibleTexts(window), text => text.Contains("isn't in this list", StringComparison.Ordinal)
                    || text.Contains("aren't in this list", StringComparison.Ordinal));
                Assert.Contains(fixture.ViewModel.ReviewCategories, category => category.Category == ReviewCategoryKind.OtherMatches);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Enter in the prompt box, then wait for the ranking to come back.
    private static void Search(SearchFixture fixture, Window window, TextBox box)
    {
        ReviewTiles.Focus(box);
        WpfReviewSelectionTests.Press(box, Key.Enter);
        WpfActionAccessibilityTests.PumpUntil(() => fixture.Search.StatusText != "Searching…");
        ReviewTiles.ShowAndDrain(window);
    }

    private static TextBox PromptBox(Window window) =>
        Assert.IsType<TextBox>(window.FindName("LikelyMatchesPromptBox"));

    private static TextBlock PlaceholderOf(Window window) =>
        Assert.IsType<TextBlock>(window.FindName("SearchPlaceholderText"));

    private static TextBlock StatusLine(Window window) =>
        Assert.IsType<TextBlock>(window.FindName("LikelyMatchesStatusText"));

    private static FrameworkElement Element(Window window, string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));

    // The tab row of the window's top bar, which holds the tab strip and the search bar.
    private static FrameworkElement HeaderRow(Window window)
    {
        var tabs = WpfReviewSelectionTests.Descendants<TabControl>(window).Single();
        return Assert.IsAssignableFrom<FrameworkElement>(tabs.Template.FindName("HeaderRow", tabs));
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root) =>
        WpfReviewSelectionTests.Descendants<TextBlock>(root).Where(text => text.IsVisible).Select(text => text.Text);

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
