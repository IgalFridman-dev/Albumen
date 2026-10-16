using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoSift.App;
using PhotoSift.App.Shell;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfGroupedReviewVirtualizationTests
{
    [Fact]
    public void TenThousandGroupsKeepRealizedRowsMaterializedRowsAndPreviewRequestsBounded()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var workflow = new GroupedReviewWorkflow(GroupedReviewData.LargeSnapshot(10_000));
            var preview = new ControllableReviewPreviewSource(autoComplete: true);
            var viewModel = new ShellViewModel(
                workflow.Scope, workflow, reviewPreviewSource: preview);
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
                var groups = Assert.IsType<ListBox>(window.FindName("ReviewGroupsList"));

                Assert.True(VirtualizingPanel.GetIsVirtualizing(groups));
                Assert.Equal(VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(groups));
                Assert.Equal(10_000, groups.Items.Count);
                Assert.True(category.Groups.MaterializedCount < 100,
                    $"Initial layout materialized {category.Groups.MaterializedCount} group rows.");
                Assert.True(RealizedContainers(groups) < 100);
                Assert.Empty(preview.Requests);

                var farItem = groups.Items[9_999];
                ListBoxItem? farContainer = null;
                for (var attempt = 0; attempt < 5 && farContainer is null; attempt++)
                {
                    groups.ScrollIntoView(farItem);
                    groups.UpdateLayout();
                    WpfReviewSelectionTests.Drain();
                    window.UpdateLayout();
                    groups.ApplyTemplate();
                    farContainer = groups.ItemContainerGenerator.ContainerFromItem(farItem)
                        as ListBoxItem;
                }

                Assert.NotNull(farContainer);
                Assert.True(RealizedContainers(groups) < 100);
                Assert.True(category.Groups.MaterializedCount < 200,
                    $"Scrolling materialized {category.Groups.MaterializedCount} group rows.");
                Assert.Empty(preview.Requests);

                var last = category.Groups[9_999];
                viewModel.OpenReviewGroupCommand.ExecuteAsync(last).GetAwaiter().GetResult();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var comparisons = Assert.IsType<ListBox>(
                    window.FindName("ReviewComparisonsList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(comparisons));
                Assert.Equal(2, comparisons.Items.Count);
                Assert.Empty(preview.Requests);

                viewModel.OpenReviewMemberCommand.ExecuteAsync(last.Members[1])
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

    private static int RealizedContainers(ItemsControl items) =>
        Descendants<ListBoxItem>(items).Count();

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
