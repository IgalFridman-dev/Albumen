using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Review;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfApprovalAccessibilityTests
{
    [Fact]
    public void ApprovalPanelDisclosesEveryCountRecoveryCoverageUnresolvedFactAndBoundaryInSourceOrder()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(selected: [1, 4]);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ApprovalPanel"));
                Assert.Equal("approval-panel", AutomationProperties.GetAutomationId(panel));
                var sourceOrder = WpfReviewSelectionTests.Descendants<TextBlock>(panel)
                    .Select(text => text.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
                AssertTextBefore(sourceOrder, "2 selected occurrences", "2 selected paths");
                AssertTextBefore(sourceOrder, "2 selected paths", "2 distinct physical files");
                AssertTextBefore(sourceOrder, "Estimated recovery for this Selection: 1024 bytes", "Incomplete Scan coverage");
                AssertTextBefore(sourceOrder, "Incomplete Scan coverage", "Unresolved total: 12");
                Assert.Contains(sourceOrder, text => text.Contains("incomplete enumeration: 1", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("failed Scan rows: 2", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("canceled Scan rows: 3", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("unprocessed Scan rows: 4", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("pending Review members: 1", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("unavailable Review members: 1", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("ordinary unselected survivors: 2", StringComparison.OrdinalIgnoreCase));
                AssertTextBefore(sourceOrder, "Windows Recycle Bin", "E1-S5 will revalidate");
                Assert.DoesNotContain(sourceOrder, text => text.Contains("safe to delete", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(sourceOrder, text => text.Contains("deleted successfully", StringComparison.OrdinalIgnoreCase));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InitialEnterCannotApproveButExplicitlyFocusedEnterAndSpaceReuseOneApprovalIdentity()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(selected: [1, 4]);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                window.Activate();
                WpfReviewSelectionTests.Drain();
                var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ApprovalPanel"));
                var approve = Assert.IsType<Button>(window.FindName("ApproveCleanupButton"));
                Assert.False(approve.IsDefault);
                Assert.False(approve.IsKeyboardFocused);

                panel.Focus();
                Keyboard.Focus(panel);
                WpfReviewSelectionTests.Press(panel, Key.Enter);
                WpfReviewSelectionTests.Drain();
                Assert.Empty(fixture.Workflow.Approvals);

                approve.Focus();
                Keyboard.Focus(approve);
                WpfReviewSelectionTests.Press(approve, Key.Enter);
                WpfReviewSelectionTests.Drain();
                approve.Focus();
                WpfReviewSelectionTests.Press(approve, Key.Space);
                WpfReviewSelectionTests.Drain();

                Assert.Equal(2, fixture.Workflow.Approvals.Count);
                Assert.Equal(fixture.Workflow.Approvals[0].IdempotencyKey,
                    fixture.Workflow.Approvals[1].IdempotencyKey);
                Assert.Equal(fixture.Workflow.Current.Review.Pin, fixture.Workflow.Approvals[0].Session);
                Assert.Equal(9, fixture.Workflow.Approvals[0].SelectionRevision);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ReviewAutomationNamesAndItemStatusExposePositionSelectionSafetyAndDisclosure()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(externalSurvivor: true, selected: [1]);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();
                var group = Assert.Single(WpfReviewSelectionTests.Descendants<FrameworkElement>(window),
                    element => AutomationProperties.GetAutomationId(element) == "exact-group");
                var groupName = AutomationProperties.GetName(group);
                var groupStatus = AutomationProperties.GetItemStatus(group);
                Assert.Contains("group 1 of 1", groupName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("4 paths", groupName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("4 physical", groupName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("1 selected", groupStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("external hard-link", groupStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("path is unknown to this Scan", groupStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Estimated recovery", groupStatus, StringComparison.OrdinalIgnoreCase);

                var selection = WpfReviewSelectionTests.Descendants<ToggleButton>(group)
                    .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");
                Assert.Contains("Eligible", AutomationProperties.GetItemStatus(selection), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("selected", AutomationProperties.GetName(selection), StringComparison.OrdinalIgnoreCase);

                var summary = Assert.IsType<TextBlock>(window.FindName("SelectionSummaryText"));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(summary));
                Assert.Contains("Incomplete", AutomationProperties.GetItemStatus(summary), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("unresolved total 12", AutomationProperties.GetItemStatus(summary), StringComparison.OrdinalIgnoreCase);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PersistenceAndStaleFailuresRemainInspectableAndEachRepeatIsAnnouncedOnce()
    {
        var fixture = ReviewShellFixture.Ready();
        var announcements = new List<string>();
        fixture.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
                announcements.Add(fixture.ViewModel.ReviewAnnouncement);
        };
        var current = fixture.Workflow.Current.Review;
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.PersistenceFailed, current);
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.PersistenceFailed, current);
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.Stale, current);
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.Stale, current);
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();

        Assert.Equal(2, announcements.Count);
        Assert.Contains("persistence", announcements[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale", announcements[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActualFocusHandlerChangesFocusWithoutApprovingSealingOrReadingEvidence()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(selected: [1]);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            window.Show();
            WpfReviewSelectionTests.Drain();
            var focusTarget = WpfReviewSelectionTests.Descendants<ToggleButton>(window)
                .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");

            var heading = Assert.IsType<TextBlock>(window.FindName("ReviewHeading"));
            Assert.True(heading.Focus());
            Keyboard.Focus(heading);
            var focusBefore = Keyboard.FocusedElement;
            Assert.Same(heading, focusBefore);
            Assert.True(focusTarget.Focus());
            Keyboard.Focus(focusTarget);
            WpfReviewSelectionTests.Drain();
            Assert.True(focusTarget.IsKeyboardFocused);
            Assert.NotSame(focusBefore, Keyboard.FocusedElement);
            AssertNoApproval(fixture);

            window.Close();
        });
    }

    [Fact]
    public void ActualWindowCloseHandlerClosesPendingOverrideWithoutApproval()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var fixture = ReviewShellFixture.Ready(selected: [1],
                dialog: new WpfReviewDialogService(() => window));
            window.DataContext = fixture.ViewModel;
            window.Show();
            WpfReviewSelectionTests.Drain();
            var before = fixture.Workflow.Current.Review;
            fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.OverrideRequired, before,
                ReviewData.Proposal(before));
            var selection = WpfReviewSelectionTests.Descendants<ToggleButton>(window)
                .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");
            Assert.True(selection.Focus());
            var commandCompleted = false;

            var interaction = WpfSurvivorOverrideDialogTests.RunModalInteraction(window,
                _ => window.Close(),
                () =>
                {
                    Assert.IsType<AsyncCommand>(selection.Command).ExecuteAsync().GetAwaiter().GetResult();
                    commandCompleted = true;
                });

            Assert.True(commandCompleted);
            Assert.True(interaction.DialogClosed);
            Assert.Equal(SurvivorOverrideDialogResult.Cancel, interaction.DialogResult);
            Assert.False(window.IsVisible);
            Assert.Equal(before.Groups[0].SelectedOccurrenceIds,
                fixture.Workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
            AssertNoApproval(fixture);
        });
    }

    [Fact]
    public void ActualApplicationExitHandlerClosesPendingOverrideWithoutApproval()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var fixture = ReviewShellFixture.Ready(selected: [1],
                dialog: new WpfReviewDialogService(() => window));
            window.DataContext = fixture.ViewModel;
            window.Show();
            WpfReviewSelectionTests.Drain();
            var before = fixture.Workflow.Current.Review;
            fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.OverrideRequired, before,
                ReviewData.Proposal(before));
            var selection = WpfReviewSelectionTests.Descendants<ToggleButton>(window)
                .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");
            Assert.True(selection.Focus());
            var commandCompleted = false;
            try
            {
                var interaction = WpfSurvivorOverrideDialogTests.RunModalInteraction(window,
                    _ => window.OnApplicationExit(window, EventArgs.Empty),
                    () =>
                    {
                        Assert.IsType<AsyncCommand>(selection.Command).ExecuteAsync().GetAwaiter().GetResult();
                        commandCompleted = true;
                    });

                Assert.True(commandCompleted);
                Assert.True(interaction.DialogClosed);
                Assert.Equal(SurvivorOverrideDialogResult.Cancel, interaction.DialogResult);
                Assert.Equal(before.Groups[0].SelectedOccurrenceIds,
                    fixture.Workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
                AssertNoApproval(fixture);
            }
            finally { window.Close(); }
        });
    }

    private static void AssertNoApproval(ReviewShellFixture fixture)
    {
        Assert.Empty(fixture.Workflow.Approvals);
        Assert.Empty(fixture.Workflow.Affirmations);
        Assert.Equal(0, fixture.Workflow.OverrideEvidenceReadAttempts);
        Assert.False(fixture.Workflow.Current.Review.Sealed);
        Assert.Null(fixture.Workflow.Current.Review.ApprovedRequest);
    }

    private static void AssertTextBefore(string[] lines, string earlier, string later)
    {
        var earlierIndex = Array.FindIndex(lines, line => line.Contains(earlier, StringComparison.OrdinalIgnoreCase));
        var laterIndex = Array.FindIndex(lines, line => line.Contains(later, StringComparison.OrdinalIgnoreCase));
        Assert.True(earlierIndex >= 0, $"Missing source-order text: {earlier}");
        Assert.True(laterIndex >= 0, $"Missing source-order text: {later}");
        Assert.True(earlierIndex < laterIndex, $"Expected '{earlier}' before '{later}'.");
    }
}
