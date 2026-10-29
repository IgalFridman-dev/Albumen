using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Albumen.App;
using Albumen.App.Shell;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class AccentLineTests
{
    [Fact]
    public void OnlyTheOpenCategoryShowsTheAccentLine()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                var categories = Assert.IsType<ListBox>(window.FindName("ReviewCategoriesList"));
                Assert.Empty(VisibleAccentLines(window, categories));

                fixture.ViewModel.OpenReviewCategoryCommand
                    .ExecuteAsync(fixture.ViewModel.ReviewCategories[1]).GetAwaiter().GetResult();
                ShowAndDrain(window);

                var line = Assert.Single(VisibleAccentLines(window, categories));
                var card = Assert.IsType<ListBoxItem>(categories.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Contains(card, Ancestors(line));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheSelectedTabShowsTheAccentLine()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                var tabs = WpfReviewSelectionTests.Descendants<TabControl>(window).Single();
                var items = tabs.Items.Cast<TabItem>().ToArray();
                var photos = items.Single(item => Equals(item.Header, "Photos"));
                var history = items.Single(item => Equals(item.Header, "History"));
                Assert.True(photos.IsSelected);
                Assert.Single(VisibleAccentLines(window, photos));
                Assert.Empty(VisibleAccentLines(window, history));

                history.IsSelected = true;
                ShowAndDrain(window);

                Assert.Empty(VisibleAccentLines(window, photos));
                Assert.Single(VisibleAccentLines(window, history));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // Igal, 2026-10-25: the steps sit on a navy panel of their own, a tone apart from the page;
    // the current step keeps its accent line and takes the accent's soft fill.
    [Fact]
    public void TheStepRailSitsOnItsOwnNavyPanel()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                var rail = Assert.IsType<ItemsControl>(window.FindName("PhaseRail"));
                var panel = Assert.IsType<Border>(window.FindName("PhaseRailPanel"));
                Assert.Contains(panel, Ancestors(rail));
                Assert.Same(window.FindResource("Albumen.RailBrush"), panel.Background);
                Assert.NotSame(window.FindResource("Albumen.WindowBrush"), panel.Background);

                var current = Assert.Single(WpfReviewSelectionTests.Descendants<Button>(rail),
                    step => step.DataContext is PhaseItemViewModel { IsCurrent: true });
                Assert.Same(window.FindResource("Albumen.AccentSoftBrush"), current.Background);
                Assert.Single(VisibleAccentLines(window, rail));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Border[] VisibleAccentLines(Window window, DependencyObject root)
    {
        var style = Assert.IsType<Style>(window.FindResource("Albumen.AccentLine"));
        return WpfReviewSelectionTests.Descendants<Border>(root)
            .Where(border => border.IsVisible && IsAccentLine(border.Style, style))
            .ToArray();
    }

    private static bool IsAccentLine(Style? style, Style accentLine)
    {
        for (var current = style; current is not null; current = current.BasedOn)
            if (ReferenceEquals(current, accentLine)) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null;
             current = VisualTreeHelper.GetParent(current))
            yield return current;
    }

    private static void ShowAndDrain(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        WpfReviewSelectionTests.Drain();
        window.UpdateLayout();
    }
}
