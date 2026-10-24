using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Albumen.App;
using Albumen.App.Shell;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfGroupedReviewVirtualizationTests
{
    [Fact]
    public void TenThousandSetsKeepRealizedSetsMaterializedRowsAndPreviewRequestsBounded()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var workflow = new GroupedReviewWorkflow(GroupedReviewData.LargeSnapshot(10_000));
            var preview = new ControllableReviewPreviewSource(autoComplete: true);
            var thumbnails = new ControllableReviewPreviewSource(autoComplete: true);
            var viewModel = new ShellViewModel(
                workflow.Scope, workflow, reviewPreviewSource: preview, reviewThumbnailSource: thumbnails);
            var category = viewModel.ReviewCategories[0];
            viewModel.OpenReviewCategoryCommand.ExecuteAsync(category).GetAwaiter().GetResult();
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 960,
                Height = 720,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var gallery = Assert.IsType<ListBox>(window.FindName("ReviewGalleryList"));

                Assert.True(VirtualizingPanel.GetIsVirtualizing(gallery));
                Assert.Equal(VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(gallery));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(gallery));
                Assert.True(ScrollViewer.GetCanContentScroll(gallery));
                Assert.Equal(10_000, gallery.Items.Count);
                Assert.True(category.Groups.MaterializedCount < 100,
                    $"Initial layout materialized {category.Groups.MaterializedCount} sets.");
                Assert.True(RealizedSets(gallery) < 100);
                Assert.NotEmpty(Tiles(gallery));
                // Only the photos in view (and a row either side) ask for their thumbnails.
                Assert.NotEmpty(thumbnails.Requests);
                Assert.True(thumbnails.Requests.Count < 40,
                    $"Initial layout asked for {thumbnails.Requests.Count} thumbnails.");
                Assert.Empty(preview.Requests);

                var farItem = gallery.Items[9_999];
                ListBoxItem? farContainer = null;
                for (var attempt = 0; attempt < 5 && farContainer is null; attempt++)
                {
                    gallery.ScrollIntoView(farItem);
                    gallery.UpdateLayout();
                    WpfReviewSelectionTests.Drain();
                    window.UpdateLayout();
                    gallery.ApplyTemplate();
                    farContainer = gallery.ItemContainerGenerator.ContainerFromItem(farItem)
                        as ListBoxItem;
                }

                Assert.NotNull(farContainer);
                Assert.True(RealizedSets(gallery) < 100);
                Assert.True(category.Groups.MaterializedCount < 200,
                    $"Scrolling materialized {category.Groups.MaterializedCount} sets.");
                Assert.True(thumbnails.Requests.Count < 40,
                    $"Scrolling asked for {thumbnails.Requests.Count} thumbnails.");
                Assert.Empty(preview.Requests);

                var last = category.Groups[9_999];
                viewModel.PlaceInViewerCommand.ExecuteAsync(
                        new PlaceInViewer(ViewerSide.Left, last.GroupId, last.Members[1]))
                    .GetAwaiter().GetResult();
                WpfReviewSelectionTests.Drain();

                Assert.Single(preview.Requests);
                Assert.Equal(last.Members[1].OccurrenceId,
                    preview.Requests[0].Occurrence.Token.OccurrenceId);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static int RealizedSets(ItemsControl items) =>
        Descendants<ListBoxItem>(items).Count();

    private static Button[] Tiles(DependencyObject root) =>
        Descendants<Button>(root)
            .Where(button => AutomationProperties.GetAutomationId(button) == "review-photo")
            .ToArray();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
