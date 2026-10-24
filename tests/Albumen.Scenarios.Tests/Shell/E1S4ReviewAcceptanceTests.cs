using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Albumen.App;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Domain.Review;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class E1S4ReviewAcceptanceTests
{
    [Fact]
    public void PointerMenuChoiceAutomationToggleKeyboardOverrideAndApprovalRemainSeparateExplicitCommands()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var fixture = ReviewShellFixture.Ready(
                dialog: new WpfReviewDialogService(() => window));
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
            window.DataContext = fixture.ViewModel;
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();

                // Pointer: "Remove" chosen from the photo's menu.
                var first = ReviewTiles.Tile(window, 1);
                var pointerCommand = Assert.IsType<AsyncCommand>(first.Member?.ToggleSelectionCommand);
                RunTrackedCommand(pointerCommand, () => ReviewTiles.Click(ReviewTiles.RemoveItem(first)));
                Assert.Equal([1L], fixture.Workflow.Current.Review.Groups[0]
                    .SelectedOccurrenceIds.Order());
                Assert.Empty(fixture.Workflow.Affirmations);
                Assert.Empty(fixture.Workflow.Approvals);

                // UI Automation: the tile's Toggle pattern unmarks it again.
                var marked = ReviewTiles.Tile(window, 1);
                var automationCommand = Assert.IsType<AsyncCommand>(marked.Member?.ToggleSelectionCommand);
                RunTrackedCommand(automationCommand, () => ReviewTiles.Toggle(marked));
                Assert.Empty(fixture.Workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
                Assert.Empty(fixture.Workflow.Affirmations);
                Assert.Empty(fixture.Workflow.Approvals);

                var beforeOverride = fixture.Workflow.Current.Review;
                var proposal = ReviewData.Proposal(beforeOverride);
                fixture.Workflow.NextSelectionResult = new(
                    ReviewCommandOutcome.OverrideRequired,
                    beforeOverride,
                    proposal);
                ReviewTiles.ShowAndDrain(window);
                // Keyboard: the Menu key opens the photo's menu, and Enter chooses "Remove".
                var fourth = ReviewTiles.Tile(window, 4);
                ReviewTiles.Focus(fourth);
                var keyboardCommand = Assert.IsType<AsyncCommand>(fourth.Member?.ToggleSelectionCommand);
                var remove = ReviewTiles.OpenMenuAtRemove(fourth);

                var interaction = WpfSurvivorOverrideDialogTests.RunModalInteraction(
                    window,
                    dialog =>
                    {
                        var affirm = Assert.IsType<Button>(
                            dialog.FindName("AffirmFinalSurvivorButton"));
                        Assert.False(affirm.IsKeyboardFocused);
                        Assert.True(affirm.Focus());
                        Keyboard.Focus(affirm);
                        WpfReviewSelectionTests.Press(affirm, Key.Space);
                    },
                    () => RunTrackedCommand(
                        keyboardCommand,
                        () => WpfReviewSelectionTests.Press(remove, Key.Enter)));

                Assert.True(interaction.DialogClosed);
                Assert.Equal(SurvivorOverrideDialogResult.Affirm, interaction.DialogResult);
                Assert.Equal(proposal, Assert.Single(fixture.Workflow.Affirmations).Proposal);
                Assert.Empty(fixture.Workflow.Approvals);
                Assert.False(fixture.Workflow.Current.Review.Sealed);
                Assert.Null(fixture.Workflow.Current.Review.ApprovedRequest);

                var approve = Assert.IsType<Button>(window.FindName("ApproveCleanupButton"));
                Assert.False(approve.IsDefault);
                var approvalPeer = UIElementAutomationPeer.CreatePeerForElement(approve);
                Assert.NotNull(approvalPeer);
                var provider = Assert.IsAssignableFrom<IInvokeProvider>(
                    approvalPeer.GetPattern(PatternInterface.Invoke));
                RunTrackedCommand(fixture.ViewModel.ApproveReviewCommand, provider.Invoke);

                var approval = Assert.Single(fixture.Workflow.Approvals);
                Assert.Equal(fixture.Workflow.Current.Review.Pin, approval.Session);
                Assert.Equal(fixture.Workflow.Current.Review.SelectionRevision,
                    approval.SelectionRevision);
                Assert.NotEqual(Guid.Empty, approval.IdempotencyKey);
                Assert.Collection(
                    fixture.Workflow.SelectionCommands,
                    pointer =>
                    {
                        Assert.Equal(SelectionCommandKind.Toggle, pointer.Kind);
                        Assert.Equal([1L], pointer.OccurrenceIds);
                    },
                    automation =>
                    {
                        Assert.Equal(SelectionCommandKind.Toggle, automation.Kind);
                        Assert.Equal([1L], automation.OccurrenceIds);
                    },
                    keyboard =>
                    {
                        Assert.Equal(SelectionCommandKind.Toggle, keyboard.Kind);
                        Assert.Equal([4L], keyboard.OccurrenceIds);
                    });
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                WpfReviewSelectionTests.Drain();
            }
        });
    }

    private static void RunTrackedCommand(AsyncCommand command, Action activation)
    {
        var started = false;
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveAvailability(object? sender, EventArgs eventArgs)
        {
            if (!command.CanExecute(null))
            {
                started = true;
            }
            else if (started)
            {
                completed.TrySetResult();
            }
        }

        command.CanExecuteChanged += ObserveAvailability;
        try
        {
            activation();
            var waitingSince = Stopwatch.GetTimestamp();
            while (!completed.Task.IsCompleted
                   && Stopwatch.GetElapsedTime(waitingSince) < TimeSpan.FromSeconds(2))
            {
                WpfReviewSelectionTests.Drain();
            }

            Assert.True(started, "The product command was not entered by the input activation.");
            Assert.True(completed.Task.IsCompleted,
                "The product command did not settle before the two-second dispatcher deadline.");
            completed.Task.GetAwaiter().GetResult();
        }
        finally
        {
            command.CanExecuteChanged -= ObserveAvailability;
        }
    }
}
