using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfSurvivorOverrideDialogTests
{
    [Fact]
    public void OpeningEnterCannotAffirmAndEscapeRestoresInvokingFocus()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var owner = new Window { Width = 400, Height = 240 };
            var invoker = new Button { Content = "Select final occurrence" };
            owner.Content = invoker;
            owner.Show();
            owner.Activate();
            invoker.Focus();
            Keyboard.Focus(invoker);
            var service = new WpfReviewDialogService(() => owner);
            var proposal = ReviewData.Proposal(ReviewData.CreateSnapshot());
            try
            {
                var result = SurvivorOverrideDialogResult.Affirm;
                var interaction = RunModalInteraction(owner, dialog =>
                {
                    var affirm = Assert.IsType<Button>(dialog.FindName("AffirmFinalSurvivorButton"));
                    var cancel = Assert.IsType<Button>(dialog.FindName("CancelSurvivorOverrideButton"));
                    var consequence = Assert.IsType<TextBlock>(dialog.FindName("OverrideConsequenceText"));
                    Assert.Equal("survivor-override-dialog", AutomationProperties.GetAutomationId(dialog));
                    Assert.False(affirm.IsDefault);
                    Assert.False(cancel.IsDefault);
                    Assert.False(affirm.IsKeyboardFocused);
                    Assert.True(consequence.IsKeyboardFocused || cancel.IsKeyboardFocused);
                    WpfReviewSelectionTests.Press(dialog, Key.Enter);
                    Assert.True(dialog.IsVisible);
                    Assert.False(affirm.IsKeyboardFocused);
                    WpfReviewSelectionTests.Press(dialog, Key.Escape);
                }, () => result = service.ShowSurvivorOverrideAsync(
                    proposal, invoker, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

                Assert.Equal(SurvivorOverrideDialogResult.Cancel, result);
                Assert.True(interaction.DialogClosed);
                Assert.True(invoker.IsKeyboardFocused);
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData(DialogActivation.PointerClick)]
    [InlineData(DialogActivation.Keyboard)]
    [InlineData(DialogActivation.Automation)]
    public void SeparateExplicitDangerActivationAffirms(DialogActivation activation)
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var owner = new Window { Content = new Button { Content = "Invoker" } };
            owner.Show();
            var invoker = Assert.IsType<Button>(owner.Content);
            invoker.Focus();
            Keyboard.Focus(invoker);
            var dialog = new SurvivorOverrideDialog(
                ReviewData.Proposal(ReviewData.CreateSnapshot())) { Owner = owner };
            try
            {
                var interaction = RunModalInteraction(owner, activeDialog =>
                {
                    var affirm = Assert.IsType<Button>(activeDialog.FindName("AffirmFinalSurvivorButton"));
                    switch (activation)
                    {
                        case DialogActivation.PointerClick:
                            dialog.OnAffirm(dialog, new RoutedEventArgs(Button.ClickEvent));
                            break;
                        case DialogActivation.Keyboard:
                            affirm.Focus();
                            Keyboard.Focus(affirm);
                            WpfReviewSelectionTests.Press(affirm, Key.Space);
                            break;
                        case DialogActivation.Automation:
                            var peer = UIElementAutomationPeer.CreatePeerForElement(affirm);
                            Assert.NotNull(peer);
                            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown activation: {activation}");
                    }
                }, () => dialog.ShowDialog());

                Assert.Equal(SurvivorOverrideDialogResult.Affirm, dialog.Result);
                Assert.True(interaction.DialogClosed);
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData(DialogCancelActivation.CancelButton)]
    [InlineData(DialogCancelActivation.Escape)]
    [InlineData(DialogCancelActivation.WindowClose)]
    public void EveryCancelPathReturnsCancelRestoresFocusAndLeavesSelectionExact(DialogCancelActivation activation)
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var owner = new Window { Content = new Button { Content = "Invoker" } };
            owner.Show();
            var invoker = Assert.IsType<Button>(owner.Content);
            invoker.Focus();
            Keyboard.Focus(invoker);
            var service = new WpfReviewDialogService(() => owner);
            var snapshot = ReviewData.CreateSnapshot(selected: [1]);
            var selectionBefore = snapshot.Groups[0].SelectedOccurrenceIds;
            try
            {
                var result = SurvivorOverrideDialogResult.Affirm;
                var interaction = RunModalInteraction(owner, dialog =>
                {
                    switch (activation)
                    {
                        case DialogCancelActivation.CancelButton:
                            var cancel = Assert.IsType<Button>(dialog.FindName("CancelSurvivorOverrideButton"));
                            var peer = UIElementAutomationPeer.CreatePeerForElement(cancel);
                            Assert.NotNull(peer);
                            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                            break;
                        case DialogCancelActivation.Escape:
                            WpfReviewSelectionTests.Press(dialog, Key.Escape);
                            break;
                        case DialogCancelActivation.WindowClose:
                            dialog.Close();
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown cancel activation: {activation}");
                    }
                }, () => result = service.ShowSurvivorOverrideAsync(
                    ReviewData.Proposal(snapshot), invoker, TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult());

                Assert.Equal(SurvivorOverrideDialogResult.Cancel, result);
                Assert.True(interaction.DialogClosed);
                Assert.Equal(selectionBefore, snapshot.Groups[0].SelectedOccurrenceIds);
                Assert.True(invoker.IsKeyboardFocused);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void ActualOverrideDialogCancelHandlerCannotApproveSealAffirmOrReadEvidence()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var owner = new MainWindow();
            var service = new WpfReviewDialogService(() => owner);
            var fixture = ReviewShellFixture.Ready(dialog: service);
            owner.DataContext = fixture.ViewModel;
            owner.Show();
            WpfReviewSelectionTests.Drain();
            try
            {
                var before = fixture.Workflow.Current.Review;
                fixture.Workflow.NextSelectionResult = new ReviewCommandResult(
                    ReviewCommandOutcome.OverrideRequired, before,
                    ReviewData.Proposal(before));
                var selection = WpfReviewSelectionTests.Descendants<ReviewSelectionCheckBox>(owner)
                    .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");
                selection.Focus();
                Keyboard.Focus(selection);
                Assert.True(selection.IsKeyboardFocused);
                var interaction = RunModalInteraction(owner, dialog =>
                {
                    var cancel = Assert.IsType<Button>(dialog.FindName("CancelSurvivorOverrideButton"));
                    var peer = UIElementAutomationPeer.CreatePeerForElement(cancel);
                    Assert.NotNull(peer);
                    Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                }, () => Assert.IsType<AsyncCommand>(selection.Command).ExecuteAsync().GetAwaiter().GetResult());

                Assert.True(interaction.DialogClosed);
                Assert.Empty(fixture.Workflow.Affirmations);
                Assert.Empty(fixture.Workflow.Approvals);
                Assert.Equal(0, fixture.Workflow.OverrideEvidenceReadAttempts);
                Assert.False(fixture.Workflow.Current.Review.Sealed);
                Assert.Null(fixture.Workflow.Current.Review.ApprovedRequest);
                Assert.Equal(before.Groups[0].SelectedOccurrenceIds,
                    fixture.Workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
                var currentSelection = WpfReviewSelectionTests.Descendants<ReviewSelectionCheckBox>(owner)
                    .Single(control => control.DataContext is ScanOccurrenceViewModel { OccurrenceId: 1 });
                Assert.Same(selection, currentSelection);
                Assert.True(selection.IsKeyboardFocused);
            }
            finally { owner.Close(); }
        });
    }

    internal static ModalInteraction RunModalInteraction(
        Window owner,
        Action<SurvivorOverrideDialog> activation,
        Action showModal)
    {
        ExceptionDispatchInfo? callbackFailure = null;
        var callbackRan = false;
        var dialogClosed = false;
        var dialogResult = SurvivorOverrideDialogResult.Cancel;
        var watchdogFired = false;
        var dispatcher = owner.Dispatcher;
        var watchdog = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        watchdog.Tick += (_, _) =>
        {
            watchdogFired = true;
            foreach (var dialog in owner.OwnedWindows.OfType<SurvivorOverrideDialog>().ToArray())
                dialog.Close();
        };
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            SurvivorOverrideDialog? dialog = null;
            try
            {
                dialog = Assert.Single(owner.OwnedWindows.OfType<SurvivorOverrideDialog>());
                dialog.Closed += (_, _) =>
                {
                    dialogClosed = true;
                    dialogResult = dialog.Result;
                };
                callbackRan = true;
                activation(dialog);
            }
            catch (Exception error)
            {
                callbackFailure = ExceptionDispatchInfo.Capture(error);
                if (dialog?.IsVisible is true) dialog.Close();
            }
        }));
        try
        {
            watchdog.Start();
            showModal();
        }
        finally
        {
            watchdog.Stop();
            foreach (var dialog in owner.OwnedWindows.OfType<SurvivorOverrideDialog>().ToArray())
                dialog.Close();
            WpfReviewSelectionTests.Drain();
        }
        callbackFailure?.Throw();
        Assert.True(callbackRan, "The modal interaction callback did not run.");
        Assert.False(watchdogFired, "The modal interaction watchdog had to unwind the dialog.");
        return new ModalInteraction(dialogClosed, dialogResult);
    }
}

public enum DialogActivation { PointerClick, Keyboard, Automation }
public enum DialogCancelActivation { CancelButton, Escape, WindowClose }
internal sealed record ModalInteraction(
    bool DialogClosed,
    SurvivorOverrideDialogResult DialogResult);
