using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
    public void PointerActivationThroughProtectedOnClickKeyboardOverrideAndAutomationApprovalRemainSeparateExplicitCommands()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var fixture = ReviewShellFixture.Ready(
                dialog: new WpfReviewDialogService(() => window));
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            window.DataContext = fixture.ViewModel;
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();

                var liveSelection = WpfReviewSelectionTests
                    .Descendants<ReviewSelectionCheckBox>(window)
                    .Single(control => control.DataContext is ScanOccurrenceViewModel
                        { OccurrenceId: 1 });
                var liveOccurrence = Assert.IsType<ScanOccurrenceViewModel>(
                    liveSelection.DataContext);
                var liveSlot = Assert.IsType<StackPanel>(
                    VisualTreeHelper.GetParent(liveSelection));
                var liveIndex = liveSlot.Children.IndexOf(liveSelection);
                Assert.True(liveIndex >= 0);
                liveSlot.Children.RemoveAt(liveIndex);
                var pointerProbe = CreateLivePointerProbe(liveOccurrence);
                liveSlot.Children.Insert(liveIndex, pointerProbe);
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                Assert.Same(pointerProbe, liveSlot.Children[liveIndex]);
                Assert.DoesNotContain(liveSelection,
                    WpfReviewSelectionTests.Descendants<ReviewSelectionCheckBox>(window));
                Assert.Same(pointerProbe,
                    WpfReviewSelectionTests.Descendants<ReviewSelectionCheckBox>(window)
                        .Single(control => control.DataContext is ScanOccurrenceViewModel
                            { OccurrenceId: 1 }));

                var pointerCommand = Assert.IsType<AsyncCommand>(pointerProbe.Command);
                RunTrackedCommand(pointerCommand, pointerProbe.ReleasePointer);
                Assert.Equal([1L], fixture.Workflow.Current.Review.Groups[0]
                    .SelectedOccurrenceIds.Order());
                Assert.Empty(fixture.Workflow.Affirmations);
                Assert.Empty(fixture.Workflow.Approvals);

                var beforeOverride = fixture.Workflow.Current.Review;
                var proposal = ReviewData.Proposal(beforeOverride);
                fixture.Workflow.NextSelectionResult = new(
                    ReviewCommandOutcome.OverrideRequired,
                    beforeOverride,
                    proposal);
                window.Activate();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var occurrences = Assert.IsType<ListBox>(window.FindName("ReviewComparisonsList"));
                var fourth = fixture.ViewModel.ActiveReviewGroup!.Members
                    .Single(member => member.OccurrenceId == 4);
                occurrences.ScrollIntoView(fourth);
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var keyboardSelection = WpfReviewSelectionTests.Descendants<ReviewSelectionCheckBox>(window)
                    .Single(control => control.DataContext is ScanOccurrenceViewModel { OccurrenceId: 4 });
                Assert.True(keyboardSelection.Focus());
                Keyboard.Focus(keyboardSelection);
                var keyboardCommand = Assert.IsType<AsyncCommand>(keyboardSelection.Command);

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
                        () => WpfReviewSelectionTests.Press(keyboardSelection, Key.Space)));

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

    private static ReviewSelectionInputProbe CreateLivePointerProbe(
        ScanOccurrenceViewModel dataContext)
    {
        var probe = new ReviewSelectionInputProbe
        {
            DataContext = dataContext,
            MinHeight = 34,
            Padding = new Thickness(4),
        };
        probe.SetBinding(ButtonBase.CommandProperty,
            new Binding(nameof(ScanOccurrenceViewModel.ToggleSelectionCommand)));
        probe.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding(nameof(ScanOccurrenceViewModel.IsSelected))
            {
                Mode = BindingMode.OneWay,
            });
        probe.SetBinding(UIElement.IsEnabledProperty,
            new Binding(nameof(ScanOccurrenceViewModel.CanSelect)));
        probe.SetBinding(ContentControl.ContentProperty,
            new Binding(nameof(ScanOccurrenceViewModel.EligibilityText)));
        AutomationProperties.SetAutomationId(probe, "selection-control");
        probe.SetBinding(AutomationProperties.NameProperty,
            new Binding(nameof(ScanOccurrenceViewModel.AutomationName)));
        probe.SetBinding(AutomationProperties.ItemStatusProperty,
            new Binding(nameof(ScanOccurrenceViewModel.ItemStatus)));
        return probe;
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
