using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// Igal, 2026-10-25: from the top, Review reads the Back button on the left on a row of its own,
// then the row of the tabs with the search bar, then the Scan's "Scan complete…" line, then how
// many sets there are to review, then the "Pick a kind…" hint, with no large gap between them.
[Collection("WPF UI Automation")]
public sealed class WpfReviewTopLayoutTests
{
    [Fact]
    public void BackSitsOnItsOwnRowAboveTheSearchRowThenTheCoverageTheSetsToReviewAndTheHint()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel, width: 1280);
            try
            {
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var tabs = WpfReviewSelectionTests.Descendants<TabControl>(window).Single();
                var headerRow = Part(tabs, "HeaderRow");
                var strip = Part(tabs, "Strip");
                var back = Named<Button>(window, "ReviewBackButton");
                var bar = Named<FrameworkElement>(window, "SearchBar");
                Assert.True(back.IsVisible && back.IsEnabled);
                Assert.True(bar.IsVisible);

                // Back: left-aligned with the tab strip, on a row of its own above the tab row.
                Assert.False(IsWithin(back, tabs));
                Assert.True(Bounds(back, root).Bottom <= Bounds(headerRow, root).Top + 0.5,
                    $"Back {Bounds(back, root)} is not above the tab row {Bounds(headerRow, root)}.");
                Assert.Equal(Bounds(strip, root).Left, Bounds(back, root).Left, 1.0);
                Assert.True(IsWithin(bar, headerRow));

                // The page opens with the coverage line right under the tab row, the sets to review
                // right under that and the hint right under them: no title between them, and the
                // count is on the page, not folded into the Scan summary.
                var page = Named<ScrollViewer>(window, "ReviewPageScrollViewer");
                var coverage = Named<TextBlock>(window, "ReviewHeading");
                var sets = Named<TextBlock>(window, "ReviewResultsSummaryText");
                var hint = Named<TextBlock>(window, "ReviewHintText");
                Assert.Equal(fixture.ViewModel.CoverageStatus, coverage.Text);
                Assert.Equal(fixture.ViewModel.ReviewResultsSummary, sets.Text);
                Assert.Contains("to review", sets.Text, StringComparison.Ordinal);
                Assert.StartsWith("Pick a kind to see its sets.", hint.Text, StringComparison.Ordinal);
                Assert.True(sets.IsVisible);
                Assert.False(IsWithin(sets, Named<Expander>(window, "ReviewScanSummaryExpander")));
                var first = WpfReviewSelectionTests.Descendants<TextBlock>(page)
                    .Where(text => text.IsVisible && text.ActualHeight > 0)
                    .MinBy(text => Bounds(text, root).Top);
                Assert.Same(coverage, first);
                Assert.InRange(Bounds(coverage, root).Top - Bounds(headerRow, root).Bottom, 0, 24);
                Assert.InRange(Bounds(sets, root).Top - Bounds(coverage, root).Bottom, 0, 8);
                Assert.InRange(Bounds(hint, root).Top - Bounds(sets, root).Bottom, 0, 8);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Like the search bar, Back shows only while Review is the current step on the Photos tab.
    [Fact]
    public void TheBackRowShowsOnlyInReviewOnThePhotosTab()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new SearchFixture();
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var back = Named<Button>(window, "ReviewBackButton");
                var breadcrumb = Named<TextBlock>(window, "ReviewBreadcrumbText");
                var tabs = WpfReviewSelectionTests.Descendants<TabControl>(window).Single().Items.Cast<TabItem>().ToArray();
                var photos = tabs.Single(item => Equals(item.Header, "Photos"));
                var history = tabs.Single(item => Equals(item.Header, "History"));
                Assert.True(back.IsVisible);
                Assert.True(breadcrumb.IsVisible);

                history.IsSelected = true;
                ReviewTiles.ShowAndDrain(window);
                Assert.False(back.IsVisible);
                Assert.False(breadcrumb.IsVisible);

                photos.IsSelected = true;
                ReviewTiles.ShowAndDrain(window);
                Assert.True(back.IsVisible);

                fixture.Workflow.Publish(fixture.Workflow.Current with { CurrentPhase = WorkflowPhase.Scan });
                ReviewTiles.ShowAndDrain(window);
                Assert.False(back.IsVisible);

                fixture.Workflow.Publish(fixture.Workflow.Current with { CurrentPhase = WorkflowPhase.Review });
                ReviewTiles.ShowAndDrain(window);
                Assert.True(back.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static T Named<T>(Window window, string name) where T : class =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static FrameworkElement Part(TabControl tabs, string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(tabs.Template.FindName(name, tabs));

    private static Rect Bounds(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
