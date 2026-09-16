using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfGroupedReviewAccessibilityTests
{
    private static readonly string[] EvidenceTextNames =
    [
        "ReviewEvidenceFullPathText",
        "ReviewEvidenceDimensionsText",
        "ReviewEvidenceSizeText",
        "ReviewEvidenceRelevantDateText",
        "ReviewEvidenceCategoryText",
        "ReviewEvidenceConfidenceText",
        "ReviewEvidenceUncertaintyText",
        "ReviewEvidenceTransformationsText",
        "ReviewEvidenceReasonText",
        "ReviewEvidenceRecommendationText",
    ];

    [Fact]
    public void PointerAndKeyboardNavigateCompareAndBackWithoutSelectingCleanup()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var window = new MainWindow
            {
                DataContext = fixture.ViewModel,
                Width = 1280,
                Height = 800,
            };
            try
            {
                ShowAndDrain(window);
                var categories = NamedList(window, "ReviewCategoriesList");
                Assert.Equal(4, categories.Items.Count);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(categories));

                var openCategory = Descendants<Button>(categories).First(button =>
                    AutomationProperties.GetAutomationId(button) == "open-review-category");
                Assert.Contains("Exact", AutomationProperties.GetName(openCategory),
                    StringComparison.Ordinal);
                Assert.Equal(1, AutomationProperties.GetPositionInSet(openCategory));
                Assert.Equal(4, AutomationProperties.GetSizeOfSet(openCategory));
                Click(openCategory);

                var groups = NamedList(window, "ReviewGroupsList");
                var groupRow = Realize(groups, groups.Items[0]);
                groupRow.Focus();
                Keyboard.Focus(groupRow);
                WpfReviewSelectionTests.Press(groupRow, Key.Enter);
                WpfReviewSelectionTests.Drain();

                var comparisons = NamedList(window, "ReviewComparisonsList");
                var openMember = Descendants<Button>(comparisons).First(button =>
                    AutomationProperties.GetAutomationId(button) == "open-review-member");
                var member = Assert.IsType<ScanOccurrenceViewModel>(openMember.DataContext);
                Assert.Equal(member.PositionInSet,
                    AutomationProperties.GetPositionInSet(openMember));
                Assert.Equal(fixture.ViewModel.ActiveReviewGroup!.MemberCount,
                    AutomationProperties.GetSizeOfSet(openMember));
                Click(openMember);

                Assert.Equal(ReviewNavigationLevel.Comparisons,
                    fixture.ViewModel.ReviewNavigationLevel);
                Assert.NotNull(fixture.ViewModel.ActiveReviewMember);
                Assert.Empty(fixture.Workflow.SelectionCommands);
                Assert.Single(fixture.Preview.Requests);

                var breadcrumb = Assert.IsType<TextBlock>(
                    window.FindName("ReviewBreadcrumbText"));
                Assert.Contains("Review", breadcrumb.Text, StringComparison.Ordinal);
                Assert.Contains("Exact", breadcrumb.Text, StringComparison.Ordinal);

                var evidence = Assert.IsAssignableFrom<FrameworkElement>(
                    window.FindName("ReviewEvidencePanel"));
                Assert.Contains("Exact", AutomationProperties.GetName(evidence),
                    StringComparison.Ordinal);
                Assert.Contains("No recommendation", AutomationProperties.GetItemStatus(evidence),
                    StringComparison.Ordinal);
                Assert.Contains("Uncertainty: Unavailable",
                    AutomationProperties.GetItemStatus(evidence), StringComparison.Ordinal);
                var preview = Assert.Single(Descendants<PreviewStatusControl>(window));
                Assert.True(preview.IsVisible);
                Assert.Same(fixture.ViewModel.ActiveReviewPreview, preview.DataContext);
                Assert.Contains("Unavailable",
                    AutomationProperties.GetItemStatus(preview), StringComparison.Ordinal);
                Assert.Equal(fixture.ViewModel.ActiveReviewMember!.ResolvedPath,
                    Assert.IsType<TextBlock>(window.FindName("ReviewEvidenceFullPathText")).Text);
                Assert.Contains("Unavailable",
                    Assert.IsType<TextBlock>(window.FindName("ReviewEvidenceDimensionsText")).Text,
                    StringComparison.Ordinal);
                Assert.Contains("512 bytes",
                    Assert.IsType<TextBlock>(window.FindName("ReviewEvidenceSizeText")).Text,
                    StringComparison.Ordinal);
                Assert.Contains("Unavailable",
                    Assert.IsType<TextBlock>(window.FindName("ReviewEvidenceRelevantDateText")).Text,
                    StringComparison.Ordinal);
                Assert.Contains("Unavailable",
                    Assert.IsType<TextBlock>(window.FindName("ReviewEvidenceTransformationsText")).Text,
                    StringComparison.Ordinal);
                Assert.Contains("Unavailable",
                    Assert.IsType<TextBlock>(window.FindName("ReviewEvidenceReasonText")).Text,
                    StringComparison.Ordinal);

                Assert.Null(window.FindName("ExactGroupsList"));
                Assert.Null(window.FindName("CouldNotAnalyzeList"));

                var back = Assert.IsType<Button>(window.FindName("ReviewBackButton"));
                Click(back);
                Assert.Equal(ReviewNavigationLevel.Groups,
                    fixture.ViewModel.ReviewNavigationLevel);
                Assert.True(groups.IsKeyboardFocusWithin);
                Click(back);
                Assert.Equal(ReviewNavigationLevel.Categories,
                    fixture.ViewModel.ReviewNavigationLevel);
                Assert.True(categories.IsKeyboardFocusWithin);
                Assert.Empty(fixture.Workflow.SelectionCommands);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectionControlAloneChangesSelectionAndRestoresComparisonFocus()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            OpenFirstComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 960, Height = 720 };
            try
            {
                ShowAndDrain(window);
                var comparisons = NamedList(window, "ReviewComparisonsList");
                var row = Realize(comparisons, comparisons.Items[0]);
                row.IsSelected = true;
                WpfReviewSelectionTests.Drain();
                Assert.Empty(fixture.Workflow.SelectionCommands);

                var selection = Assert.Single(Descendants<ReviewSelectionCheckBox>(row));
                Assert.Contains("Not selected", AutomationProperties.GetName(selection),
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Eligible", AutomationProperties.GetItemStatus(selection),
                    StringComparison.Ordinal);
                selection.Focus();
                Keyboard.Focus(selection);
                Toggle(selection);
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();

                var command = Assert.Single(fixture.Workflow.SelectionCommands);
                Assert.Equal(SelectionCommandKind.Toggle, command.Kind);
                Assert.Equal([1], command.OccurrenceIds);
                Assert.Equal(ReviewNavigationLevel.Comparisons,
                    fixture.ViewModel.ReviewNavigationLevel);
                var current = NamedList(window, "ReviewComparisonsList");
                Assert.Contains(Descendants<ReviewSelectionCheckBox>(current),
                    control => control.IsKeyboardFocusWithin
                        && control.DataContext is ScanOccurrenceViewModel member
                        && member.OccurrenceId == 1);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DeliberateOverrideFromGroupedComparisonUsesTheFocusedSelectionControl()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var dialog = new RecordingReviewDialogService
            {
                Result = SurvivorOverrideDialogResult.Affirm,
            };
            var fixture = GroupedWpfFixture.Create(dialog);
            var proposal = ReviewData.Proposal(fixture.Workflow.Current.Review);
            fixture.Workflow.NextSelectionResult = new(
                ReviewCommandOutcome.OverrideRequired,
                fixture.Workflow.Current.Review,
                proposal);
            OpenFirstComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                ShowAndDrain(window);
                var selection = Descendants<ReviewSelectionCheckBox>(
                    NamedList(window, "ReviewComparisonsList")).First();
                selection.Focus();
                Keyboard.Focus(selection);
                Toggle(selection);
                WpfReviewSelectionTests.Drain();

                Assert.Equal([proposal], dialog.Proposals);
                Assert.Single(fixture.Workflow.Affirmations);
                Assert.True(selection.IsKeyboardFocusWithin);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WideCompactAndStackedModesRetainDynamicBrushesAndScaledReadableEvidence()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            var window = new MainWindow
            {
                DataContext = fixture.ViewModel,
                Width = 1280,
                Height = 800,
                FontSize = 24,
            };
            try
            {
                ShowAndDrain(window);
                var categoriesPanel = NamedElement(window, "ReviewCategoriesPanel");
                var groupsPanel = NamedElement(window, "ReviewGroupsPanel");
                var comparisonsPanel = NamedElement(window, "ReviewComparisonsPanel");
                var navigation = NamedElement(window, "ReviewNavigationGrid");
                Assert.True(categoriesPanel.IsVisible);
                Assert.True(groupsPanel.IsVisible);
                Assert.True(comparisonsPanel.IsVisible);
                Assert.All(new[] { categoriesPanel, groupsPanel, comparisonsPanel }, panel =>
                    Assert.True(panel.ActualWidth >= navigation.ActualWidth / 3 - 12));
                Assert.True(categoriesPanel.ActualWidth + groupsPanel.ActualWidth
                    + comparisonsPanel.ActualWidth >= navigation.ActualWidth - 20);

                Click(Descendants<Button>(categoriesPanel).First(button =>
                    AutomationProperties.GetAutomationId(button) == "open-review-category"));
                window.Width = 900;
                ShowAndDrain(window);
                Assert.True(categoriesPanel.IsVisible);
                Assert.True(groupsPanel.IsVisible);
                Assert.False(comparisonsPanel.IsVisible);
                Assert.True(categoriesPanel.ActualWidth >= navigation.ActualWidth / 2 - 12);
                Assert.True(groupsPanel.ActualWidth >= navigation.ActualWidth / 2 - 12);
                Assert.True(categoriesPanel.ActualWidth + groupsPanel.ActualWidth >=
                    navigation.ActualWidth - 20);

                window.Width = 680;
                ShowAndDrain(window);
                Assert.False(categoriesPanel.IsVisible);
                Assert.True(groupsPanel.IsVisible);
                Assert.False(comparisonsPanel.IsVisible);
                Assert.True(groupsPanel.ActualWidth >= navigation.ActualWidth - 12);

                var activeBorder = new SolidColorBrush(Color.FromRgb(12, 34, 56));
                var windowBrush = new SolidColorBrush(Color.FromRgb(1, 2, 3));
                var textBrush = new SolidColorBrush(Color.FromRgb(250, 249, 248));
                window.Resources[SystemColors.ActiveBorderBrushKey] = activeBorder;
                window.Resources[SystemColors.WindowBrushKey] = windowBrush;
                window.Resources[SystemColors.WindowTextBrushKey] = textBrush;
                WpfReviewSelectionTests.Drain();

                Assert.Same(windowBrush, window.Background);
                Assert.Same(textBrush, window.Foreground);
                Assert.All(new[] { categoriesPanel, groupsPanel, comparisonsPanel }, panel =>
                    Assert.Same(activeBorder, Assert.IsAssignableFrom<Border>(panel).BorderBrush));
                foreach (var name in EvidenceTextNames)
                {
                    Assert.Equal(TextWrapping.Wrap,
                        Assert.IsType<TextBlock>(window.FindName(name)).TextWrapping);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void OpenFirstComparison(ShellViewModel viewModel)
    {
        var category = viewModel.ReviewCategories[0];
        viewModel.OpenReviewCategoryCommand.ExecuteAsync(category).GetAwaiter().GetResult();
        viewModel.OpenReviewGroupCommand.ExecuteAsync(category.Groups[0]).GetAwaiter().GetResult();
    }

    private static void ShowAndDrain(Window window)
    {
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        WpfReviewSelectionTests.Drain();
        window.UpdateLayout();
    }

    private static FrameworkElement NamedElement(MainWindow window, string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));

    private static ListBox NamedList(MainWindow window, string name) =>
        Assert.IsType<ListBox>(window.FindName(name));

    private static ListBoxItem Realize(ListBox list, object item)
    {
        list.ScrollIntoView(item);
        list.UpdateLayout();
        WpfReviewSelectionTests.Drain();
        return Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WpfReviewSelectionTests.Drain();
    }

    private static void Toggle(ReviewSelectionCheckBox selection)
    {
        var peer = UIElementAutomationPeer.FromElement(selection)
            ?? UIElementAutomationPeer.CreatePeerForElement(selection);
        Assert.IsAssignableFrom<IToggleProvider>(
            peer.GetPattern(PatternInterface.Toggle)).Toggle();
        WpfReviewSelectionTests.Drain();
    }

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

internal sealed class GroupedWpfFixture
{
    private GroupedWpfFixture(
        ReviewStaticScope scope,
        RecordingReviewWorkflow workflow,
        ControllableReviewPreviewSource preview,
        IReviewDialogService dialog)
    {
        Workflow = workflow;
        Preview = preview;
        ViewModel = new ShellViewModel(scope, workflow, dialog, reviewPreviewSource: preview);
    }

    public RecordingReviewWorkflow Workflow { get; }
    public ControllableReviewPreviewSource Preview { get; }
    public ShellViewModel ViewModel { get; }

    public static GroupedWpfFixture Create(IReviewDialogService? dialog = null)
    {
        var scope = new ReviewStaticScope();
        var workflow = new RecordingReviewWorkflow(
            scope.Current, ReviewData.CreateSnapshot(twoGroups: true));
        return new(scope, workflow,
            new ControllableReviewPreviewSource(autoComplete: true),
            dialog ?? new RecordingReviewDialogService());
    }
}
