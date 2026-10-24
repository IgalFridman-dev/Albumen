using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Albumen.App;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Application.Shell;
using Albumen.Domain.Review;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfGroupedReviewAccessibilityTests
{
    private static readonly string[] ReviewDetailNames =
        ["ReviewEvidencePanel", "SelectionSummaryText", "ApproveCleanupButton"];

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
                Assert.Contains("Identical copies", AutomationProperties.GetName(openCategory),
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
                Assert.Same(member, fixture.ViewModel.LeftViewer.Member);
                Assert.Empty(fixture.Workflow.SelectionCommands);
                Assert.Single(fixture.Preview.Requests);

                var breadcrumb = Assert.IsType<TextBlock>(
                    window.FindName("ReviewBreadcrumbText"));
                Assert.Contains("Review", breadcrumb.Text, StringComparison.Ordinal);
                Assert.Contains("Identical copies", breadcrumb.Text, StringComparison.Ordinal);

                var viewer = Assert.IsAssignableFrom<FrameworkElement>(
                    window.FindName("ReviewEvidencePanel"));
                Assert.Equal($"Left Viewer: {member.FileName}", AutomationProperties.GetName(viewer));
                Assert.Equal(member.ItemStatus, AutomationProperties.GetItemStatus(viewer));
                var preview = Assert.Single(Descendants<PreviewStatusControl>(window));
                Assert.True(preview.IsVisible);
                Assert.Same(fixture.ViewModel.LeftViewer.Preview, preview.DataContext);
                Assert.Contains("Unavailable",
                    AutomationProperties.GetItemStatus(preview), StringComparison.Ordinal);

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
                Assert.StartsWith("Kept:", AutomationProperties.GetName(selection),
                    StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("Kept;", AutomationProperties.GetItemStatus(selection),
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
    public void AMarkHeldInAnotherSetIsShownCheckedAndDisabledAndItsSettledSetIsDimmed()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create(
                review: scope => GroupedReviewData.OverlappingSnapshot(scope).Review);
            var near = fixture.ViewModel.ReviewCategories[1];
            fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(near).GetAwaiter().GetResult();
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1280, Height = 800 };
            try
            {
                ShowAndDrain(window);
                var groups = NamedList(window, "ReviewGroupsList");
                var groupRow = Realize(groups, groups.Items[0]);
                var caption = Descendants<TextBlock>(groupRow).Single(text =>
                    text.Text == "Nothing left to decide: 1 marked elsewhere");
                Assert.True(caption.IsVisible);
                var card = Descendants<Grid>(groupRow).First(grid => Descendants<TextBlock>(grid).Contains(caption));
                Assert.Equal(SystemParameters.HighContrast ? 1.0 : 0.55, card.Opacity, 2);

                fixture.ViewModel.OpenReviewGroupCommand.ExecuteAsync(near.Groups[0]).GetAwaiter().GetResult();
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();
                var comparisons = NamedList(window, "ReviewComparisonsList");
                var row = Realize(comparisons, comparisons.Items[0]);
                var selection = Assert.Single(Descendants<ReviewSelectionCheckBox>(row));
                Assert.False(selection.IsEnabled);
                Assert.True(selection.IsChecked);
                Assert.Equal("Marked for removal in Identical copies set 1", selection.Content);
                Assert.StartsWith("Marked for removal:", AutomationProperties.GetName(selection),
                    StringComparison.Ordinal);
                Assert.StartsWith("Marked for removal in Identical copies set 1;",
                    AutomationProperties.GetItemStatus(selection), StringComparison.Ordinal);
                var kept = Assert.Single(Descendants<ReviewSelectionCheckBox>(
                    Realize(comparisons, comparisons.Items[1])));
                Assert.True(kept.IsEnabled);
                Assert.Equal("Kept", kept.Content);

                var openMember = Descendants<Button>(row).First(button =>
                    AutomationProperties.GetAutomationId(button) == "open-review-member");
                Click(openMember);
                Assert.Equal(1, fixture.ViewModel.LeftViewer.Member?.OccurrenceId);
                Assert.Empty(fixture.Workflow.SelectionCommands);
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
                window.Resources["Albumen.BorderBrush"] = activeBorder;
                window.Resources["Albumen.WindowBrush"] = windowBrush;
                window.Resources["Albumen.TextBrush"] = textBrush;
                WpfReviewSelectionTests.Drain();

                Assert.Same(windowBrush, window.Background);
                Assert.Same(textBrush, window.Foreground);
                Assert.All(new[] { categoriesPanel, groupsPanel, comparisonsPanel }, panel =>
                    Assert.Same(activeBorder, Assert.IsAssignableFrom<Border>(panel).BorderBrush));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TallPortraitEvidenceIsScrollableWhileSelectionAndApprovalRemainReachable()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            OpenFirstComparison(fixture.ViewModel);
            var window = new MainWindow
            {
                DataContext = fixture.ViewModel,
                Width = 960,
                Height = 720,
                FontSize = 24,
            };
            try
            {
                ShowAndDrain(window);
                var preview = Assert.IsType<PreviewStatusControl>(
                    window.FindName("ReviewPreviewStatusControl"));
                preview.Height = 512;
                ShowAndDrain(window);

                var evidence = NamedElement(window, "ReviewEvidencePanel");
                var evidenceScroller = Ancestors(evidence).OfType<ScrollViewer>().FirstOrDefault();
                Assert.NotNull(evidenceScroller);
                Assert.True(evidenceScroller.ViewportHeight < evidenceScroller.ExtentHeight);
                var selectionSummary = NamedElement(window, "SelectionSummaryText");
                selectionSummary.BringIntoView();
                ShowAndDrain(window);
                AssertInsideWindow(window, selectionSummary);
                var approve = NamedElement(window, "ApproveCleanupButton");
                approve.BringIntoView();
                ShowAndDrain(window);
                AssertInsideWindow(window, approve);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(double.NaN, double.NaN, 12d)]
    [InlineData(680d, 520d, 12d)]
    [InlineData(680d, 520d, 24d)]
    public void ReviewPageReachesComparisonSelectionAndApprovalWithoutMaximizing(
        double width, double height, double fontSize)
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = GroupedWpfFixture.Create();
            OpenFirstComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel, FontSize = fontSize };
            if (!double.IsNaN(width)) window.Width = width;
            if (!double.IsNaN(height)) window.Height = height;
            try
            {
                ShowAndDrain(window);
                var page = Assert.IsType<ScrollViewer>(window.FindName("ReviewPageScrollViewer"));
                var navigation = NamedElement(window, "ReviewNavigationGrid");
                Assert.True(page.IsVisible);
                Assert.True(navigation.ActualHeight <= page.ViewportHeight,
                    $"Navigation height {navigation.ActualHeight} exceeded page viewport {page.ViewportHeight}.");

                var comparisons = NamedList(window, "ReviewComparisonsList");
                Assert.True(comparisons.IsVisible);
                var selection = Descendants<ReviewSelectionCheckBox>(
                    Realize(comparisons, comparisons.Items[0])).Single();
                selection.BringIntoView();
                ShowAndDrain(window);
                AssertInsideViewport(page, selection);
                AssertInsideWindow(window, selection);

                foreach (var name in ReviewDetailNames)
                {
                    var element = NamedElement(window, name);
                    element.BringIntoView();
                    ShowAndDrain(window);
                    AssertInsideViewport(page, element);
                }
                AssertInsideWindow(window, NamedElement(window, "ApproveCleanupButton"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    // PM-016: at the default window size the category list holds two or three cards, and a
    // wheel notch in item scrolling moved three items, so the two middle categories were never
    // shown at the top. Large text makes the cards tall enough to pin the overflow here. The
    // wheel is raised on the card at the top of the list, where the pointer rests, so the event
    // takes the real route: tunnelling through the list's handler and bubbling up to the list's
    // ScrollViewer. A card that scrolled out of view leaves the visual tree, so it is looked up
    // again before every notch.
    [Fact]
    public void OneWheelNotchMovesTheCategoryListByOneCardSoTheMiddleCardsAreReachable()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = OverflowingCategoriesWindow(out var categories, out var scroller);
            try
            {
                Wheel(TopCard(categories, scroller), -120);
                Assert.Equal(1, scroller.VerticalOffset);
                Wheel(TopCard(categories, scroller), -120);
                Assert.Equal(2, scroller.VerticalOffset);
                Wheel(TopCard(categories, scroller), 120);
                Assert.Equal(1, scroller.VerticalOffset);
            }
            finally { window.Close(); }
        });
    }

    // A precision touchpad reports a notch as several small deltas; they add up to one card.
    [Fact]
    public void SmallTouchpadDeltasAddUpToOneCardPerNotch()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = OverflowingCategoriesWindow(out var categories, out var scroller);
            try
            {
                Wheel(TopCard(categories, scroller), -40);
                Assert.Equal(0, scroller.VerticalOffset);
                Wheel(TopCard(categories, scroller), -40);
                Assert.Equal(0, scroller.VerticalOffset);
                Wheel(TopCard(categories, scroller), -40);
                Assert.Equal(1, scroller.VerticalOffset);
            }
            finally { window.Close(); }
        });
    }

    // At the end of the list a further notch scrolls the page instead, so Approval stays
    // reachable while the pointer rests over the list.
    [Fact]
    public void AWheelNotchAtTheEndOfTheCategoryListScrollsThePage()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = OverflowingCategoriesWindow(out _, out var scroller);
            try
            {
                var page = Assert.IsType<ScrollViewer>(window.FindName("ReviewPageScrollViewer"));
                scroller.ScrollToEnd();
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();
                var end = scroller.VerticalOffset;
                Assert.True(end > 0, "The list did not scroll to its end.");
                Assert.Equal(0, page.VerticalOffset);

                Wheel(scroller, -120);
                Assert.Equal(end, scroller.VerticalOffset);
                Assert.Equal(Math.Min(40, page.ScrollableHeight), page.VerticalOffset);
            }
            finally { window.Close(); }
        });
    }

    private static MainWindow OverflowingCategoriesWindow(out ListBox categories, out ScrollViewer scroller)
    {
        var fixture = GroupedWpfFixture.Create();
        var window = new MainWindow
        {
            DataContext = fixture.ViewModel,
            Width = 960,
            Height = 720,
            FontSize = 24,
        };
        ShowAndDrain(window);
        categories = NamedList(window, "ReviewCategoriesList");
        Assert.Equal(4, categories.Items.Count);
        scroller = Descendants<ScrollViewer>(categories).First();
        Assert.True(scroller.ScrollableHeight >= 2,
            $"The four cards must overflow the list by two items; ScrollableHeight is {scroller.ScrollableHeight}.");
        Assert.Equal(0, scroller.VerticalOffset);
        return window;
    }

    private static Button TopCard(ListBox list, ScrollViewer scroller)
    {
        var container = Assert.IsType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromIndex((int)scroller.VerticalOffset));
        var card = Descendants<Button>(container).First();
        Assert.True(card.IsVisible, "The card at the top of the list is not shown.");
        return card;
    }

    // Input raises the tunnelling preview first and the bubbling event only when it was not handled.
    private static void Wheel(UIElement element, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = element,
        };
        element.RaiseEvent(preview);
        if (!preview.Handled)
        {
            element.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = element,
            });
        }
        WpfReviewSelectionTests.Drain();
        element.UpdateLayout();
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

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null;
             current = VisualTreeHelper.GetParent(current))
            yield return current;
    }

    private static void AssertInsideViewport(ScrollViewer viewport, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(viewport).TransformBounds(
            new Rect(new Point(), element.RenderSize));
        // An element taller than the viewport is brought into view top first.
        var fits = bounds.Height <= viewport.ViewportHeight;
        Assert.True(bounds.Top >= -0.5 && (fits ? bounds.Bottom <= viewport.ViewportHeight + 0.5 : bounds.Top <= 0.5),
            $"{element.Name} bounds {bounds} were outside the Review viewport height {viewport.ViewportHeight}.");
    }

    private static void AssertInsideWindow(Window window, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(
            new Rect(new Point(), element.RenderSize));
        Assert.True(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
            $"{element.Name} bounds {bounds} were outside window height {window.ActualHeight}.");
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

    public static GroupedWpfFixture Create(IReviewDialogService? dialog = null,
        Func<ShellSnapshot, ReviewSnapshot>? review = null)
    {
        var scope = new ReviewStaticScope();
        var workflow = new RecordingReviewWorkflow(
            scope.Current, review?.Invoke(scope.Current) ?? ReviewData.CreateSnapshot(twoGroups: true));
        return new(scope, workflow,
            new ControllableReviewPreviewSource(autoComplete: true),
            dialog ?? new RecordingReviewDialogService());
    }
}
