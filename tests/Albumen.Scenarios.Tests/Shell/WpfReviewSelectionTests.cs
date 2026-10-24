using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Albumen.App;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Shell;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfReviewSelectionTests
{
    [Fact]
    public void VisibleReviewPhaseNavigationAdmitsFreshScanWithoutResettingAnExistingReview()
    {
        RunOnSta(() =>
        {
            var scope = new ExactShellFixture.StaticScope();
            var scheduler = new ManualWorkflowScheduler();
            var scan = new CompletedScanOwner();
            var review = new OpeningReviewOwner();
            var workflow = new AlbumenWorkflow(scope, scan, scheduler, review);
            var viewModel = new ShellViewModel(scope, workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                FinishOnSta(viewModel.ContinueCommand.ExecuteAsync(), scheduler);
                FinishOnSta(viewModel.ConfirmExactRulesCommand.ExecuteAsync(), scheduler);
                scan.Publish(CompletedScanOwner.CreateCompleted());
                scheduler.Drain();
                window.Show();
                Drain();

                InvokeReviewPhase(window, viewModel, scheduler);

                var admittedPin = workflow.Current.Review.Pin;
                Assert.NotNull(admittedPin);
                Assert.Equal(1, review.OpenCount);
                Assert.True(viewModel.IsReviewPhase);

                review.Publish(workflow.Current.Review with { Sealed = true });
                scheduler.Drain();
                FinishOnSta(viewModel.NavigateCommand.ExecuteAsync(WorkflowPhase.Scan), scheduler);
                InvokeReviewPhase(window, viewModel, scheduler);

                Assert.Equal(1, review.OpenCount);
                Assert.Equal(admittedPin, workflow.Current.Review.Pin);
                Assert.True(workflow.Current.Review.Sealed);
            }
            finally
            {
                try
                {
                    window.Close();
                }
                finally
                {
                    FinishOnSta(viewModel.DisposeAsync().AsTask(), scheduler);
                }
            }
        });
    }

    [Fact]
    public void ReviewProjectsEmptySelectionEligibilityReasonsPathsAndPhysicalEvidence()
    {
        var fixture = ReviewShellFixture.Ready();
        var group = Assert.Single(fixture.ViewModel.ExactGroups);

        Assert.Empty(group.SelectedOccurrenceIds);
        Assert.Equal(4, group.Members.Length);
        Assert.True(group.Members[0].CanSelect);
        Assert.Equal("Kept", group.Members[0].EligibilityText);
        Assert.False(group.Members[1].CanSelect);
        Assert.Contains("checked again", group.Members[1].EligibilityText);
        Assert.False(group.Members[2].CanSelect);
        Assert.Contains("changed since the scan", group.Members[2].EligibilityText);
        Assert.True(group.Members[3].CanSelect);
        Assert.Contains(@"C:\Photos\照片-very-long-name-one.jpg", group.Members[0].DiscoveredPath);
        Assert.Contains("Volume", group.Members[0].PhysicalIdentity);
        Assert.Contains("links: 1", group.Members[0].Allocation);
        Assert.Contains("0 selected", fixture.ViewModel.SelectionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleRangeBulkAndFocusedSelectAllUseCompleteCurrentCoordinatorPins()
    {
        var fixture = ReviewShellFixture.Ready(twoGroups: true);
        var first = fixture.ViewModel.ExactGroups[0];
        var second = fixture.ViewModel.ExactGroups[1];

        await Assert.IsType<AsyncCommand>(first.Members[0].ToggleSelectionCommand).ExecuteAsync();
        await first.ReplaceSelectionCommand.ExecuteAsync(new long[] { 1, 4 });
        await first.SelectAllCommand.ExecuteAsync();
        await second.SelectAllCommand.ExecuteAsync();

        Assert.Collection(fixture.Workflow.SelectionCommands,
            command => AssertSelection(command, first, 9, SelectionCommandKind.Toggle, [1]),
            command => AssertSelection(command, first, 10, SelectionCommandKind.ReplaceFocusedGroup, [1, 4]),
            command => AssertSelection(command, first, 11, SelectionCommandKind.ReplaceFocusedGroup, [1, 4]),
            command => AssertSelection(command, second, 12, SelectionCommandKind.ReplaceFocusedGroup, [11, 14]));
        Assert.DoesNotContain(11, fixture.Workflow.SelectionCommands[2].OccurrenceIds);
    }

    [Fact]
    public async Task AtomicRejectionLeavesProjectedSelectionExactlyAtReturnedSnapshot()
    {
        var fixture = ReviewShellFixture.Ready();
        var before = fixture.ViewModel.ExactGroups[0].SelectedOccurrenceIds;
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.Stale, fixture.Workflow.Current.Review);

        await fixture.ViewModel.ExactGroups[0].ReplaceSelectionCommand.ExecuteAsync(new long[] { 1, 4 });

        Assert.Equal(before, fixture.ViewModel.ExactGroups[0].SelectedOccurrenceIds);
        Assert.All(fixture.ViewModel.ExactGroups[0].Members, member => Assert.False(member.IsSelected));
        Assert.Equal("The review changed in the meantime, so nothing was changed. Try again.",
            fixture.ViewModel.ReviewAnnouncement);
    }

    [Fact]
    public void ExplicitDialogAffirmationForwardsTheExactProposalWithoutApproving()
    {
        RunOnSta(() =>
        {
            var invoker = new Button { Content = "Invoker" };
            var window = new Window { Content = invoker };
            window.Show();
            invoker.Focus();
            Keyboard.Focus(invoker);
            var dialog = new RecordingReviewDialogService { Result = SurvivorOverrideDialogResult.Affirm };
            var fixture = ReviewShellFixture.Ready(dialog: dialog);
            var before = fixture.Workflow.Current.Review;
            var proposal = ReviewData.Proposal(before);
            fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.OverrideRequired, before, proposal);
            try
            {
                Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync()
                    .GetAwaiter().GetResult();

                Assert.Equal([proposal], dialog.Proposals);
                var affirmation = Assert.Single(fixture.Workflow.Affirmations);
                Assert.Equal(proposal, affirmation.Proposal);
                Assert.NotEqual(Guid.Empty, affirmation.EvidenceId.Value);
                Assert.Empty(fixture.Workflow.Approvals);
                Assert.Equal(0, fixture.Workflow.OverrideEvidenceReadAttempts);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExternalSurvivorAndSelectionRecoveryNeverMasqueradeAsPotentialRecovery()
    {
        var fixture = ReviewShellFixture.Ready(externalSurvivor: true, selected: [1]);
        var group = Assert.Single(fixture.ViewModel.ExactGroups);

        Assert.Contains("external hard-link", group.SurvivorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path is unknown to this Scan", group.SurvivorText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Potential recovery", group.PotentialRecovery);
        Assert.Contains("Estimated recovery for this Selection: 0 bytes", group.SelectionRecovery);
        Assert.NotEqual(group.PotentialRecovery, group.SelectionRecovery);
    }

    [Fact]
    public async Task DelayedOlderCommandResultCannotReplaceTheNewerPublishedReview()
    {
        var fixture = ReviewShellFixture.Ready();
        var initial = fixture.Workflow.Current.Review;
        var delayed = fixture.Workflow.DeferNextSelection();
        var command = fixture.ViewModel.ExactGroups[0].ReplaceSelectionCommand.ExecuteAsync(new long[] { 1 });
        var newer = ReviewData.WithSelection(initial, [4], 11);
        fixture.Workflow.Publish(newer);

        delayed.SetResult(new(ReviewCommandOutcome.Committed,
            ReviewData.WithSelection(initial, [1], 10)));
        await command;

        var group = Assert.Single(fixture.ViewModel.ExactGroups);
        Assert.Equal(11, group.SelectionRevision);
        Assert.Equal([4], group.SelectedOccurrenceIds.Order().ToArray());
    }

    // The tile's menu, UI Automation's Toggle and the viewer's Remove button send one Toggle
    // each through the same set command; Space on the tile places it, never marks it.
    [Fact]
    public void MenuKeyboardAndAutomationActivationShareTheToggleCommand()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready();
            OpenIdenticalCopies(fixture.ViewModel);
            var neutral = new ReviewCommandResult(ReviewCommandOutcome.Neutral, fixture.Workflow.Current.Review);
            fixture.Workflow.NextSelectionResult = neutral;
            fixture.Workflow.NextSelectionResult = neutral;
            fixture.Workflow.NextSelectionResult = neutral;
            var window = ReviewTiles.Show(fixture.ViewModel);
            try
            {
                var tile = ReviewTiles.Tile(window, 1);
                var command = Assert.IsType<AsyncCommand>(tile.Member!.ToggleSelectionCommand);

                ReviewTiles.Click(ReviewTiles.RemoveItem(tile));
                Assert.Single(fixture.Workflow.SelectionCommands);
                Assert.True(tile.IsKeyboardFocused);

                ReviewTiles.Toggle(tile);
                Assert.Equal(2, fixture.Workflow.SelectionCommands.Count);
                Assert.True(tile.IsKeyboardFocused);

                Press(tile, Key.Space);
                Drain();
                Assert.Equal(1, fixture.ViewModel.LeftViewer.Member?.OccurrenceId);
                Assert.Equal(2, fixture.Workflow.SelectionCommands.Count);

                var viewerButton = Assert.IsType<Button>(window.FindName("LeftViewerRemoveButton"));
                ReviewTiles.Focus(viewerButton);
                ReviewTiles.Invoke(viewerButton);

                Assert.Equal(3, fixture.Workflow.SelectionCommands.Count);
                Assert.All(fixture.Workflow.SelectionCommands, sent =>
                    AssertSelection(sent, fixture.ViewModel.ExactGroups[0], SelectionCommandKind.Toggle, [1]));
                Assert.Same(command, tile.Member!.ToggleSelectionCommand);
                Assert.True(command.CanExecute(null));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RoutedCtrlASelectsOnlyTheGroupContainingKeyboardFocus()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(twoGroups: true);
            OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel, 960, 720);
            try
            {
                var gallery = Assert.IsType<ListBox>(window.FindName("ReviewGalleryList"));
                Assert.Equal(2, gallery.Items.Count);
                var focusedTile = ReviewTiles.Tile(window, 1);
                ReviewTiles.Focus(focusedTile);
                var selectionRevision = fixture.ViewModel.ExactGroups[0].SelectionRevision;

                ApplicationCommands.SelectAll.Execute(null, focusedTile);
                Drain();

                var sent = Assert.Single(fixture.Workflow.SelectionCommands);
                AssertSelection(sent, fixture.ViewModel.ExactGroups[0], selectionRevision,
                    SelectionCommandKind.ReplaceFocusedGroup, [1, 4]);
                Assert.DoesNotContain(11, sent.OccurrenceIds);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void MenuCommitRestoresFocusToTheReplacementControlForTheSameOccurrence()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready();
            OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel, 960, 720);
            try
            {
                var original = ReviewTiles.Tile(window, 1);
                ReviewTiles.Focus(original);

                ReviewTiles.Click(ReviewTiles.RemoveItem(original));
                Drain();

                var replacement = ReviewTiles.Tile(window, 1);
                Assert.NotSame(original, replacement);
                Assert.True(replacement.IsKeyboardFocused);
                Assert.True(replacement.IsEnabled);
                Assert.True(Assert.IsType<AsyncCommand>(replacement.Member!.ToggleSelectionCommand).CanExecute(null));
            }
            finally { window.Close(); }
        });
    }

    // The gallery shows a set's photos whole, so the late photo here is in the last of 10,000
    // sets: giving focus back to it realizes only the sets around it.
    [Fact]
    public void LateMemberFocusRestorationDoesNotMaterializeAllPrecedingComparisons()
    {
        RunOnSta(() =>
        {
            const int setCount = 10_000;
            var scope = new ReviewStaticScope();
            var review = GroupedReviewData.LargeSnapshot(setCount)(scope.Current).Review;
            var workflow = new RecordingReviewWorkflow(scope.Current, review);
            var viewModel = new ShellViewModel(scope, workflow);
            OpenIdenticalCopies(viewModel);
            var window = ReviewTiles.Show(viewModel, 960, 720);
            try
            {
                var gallery = Assert.IsType<ListBox>(window.FindName("ReviewGalleryList"));
                var lateSet = RealizeContainer(gallery, viewModel.ActiveReviewGroups![setCount - 1]);
                var original = Descendants<ReviewPhotoTile>(lateSet).First();
                var late = original.Member!.OccurrenceId;
                ReviewTiles.Focus(original);

                var marked = review.Groups[setCount - 1];
                workflow.Publish(review with
                {
                    SelectionRevision = 10,
                    Groups = review.Groups.SetItem(setCount - 1, marked with
                    {
                        SelectedOccurrenceIds = [late],
                        Members = [.. marked.Members.Select(member => member with
                            { Selected = member.Occurrence.Token.OccurrenceId == late })],
                    }),
                });
                Drain();

                Assert.True(viewModel.ActiveReviewGroups!.MaterializedCount < 200,
                    $"Focus restoration materialized {viewModel.ActiveReviewGroups.MaterializedCount} sets.");
                var focused = Assert.IsType<ReviewPhotoTile>(Keyboard.FocusedElement);
                Assert.Equal(late, focused.Member?.OccurrenceId);
                Assert.NotSame(original, focused);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void QueuedRestoreDoesNotStealNewerDeliberateReviewFocus()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready();
            OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel, 960, 720);
            try
            {
                ReviewTiles.Focus(ReviewTiles.Tile(window, 1));

                fixture.Workflow.Publish(ReviewData.WithSelection(
                    fixture.Workflow.Current.Review, [1], 10));
                window.UpdateLayout();
                var deliberate = ReviewTiles.Tile(window, 4);
                ReviewTiles.Focus(deliberate);

                Drain();

                Assert.True(deliberate.IsKeyboardFocused);
                Assert.Equal(4, Assert.IsType<ReviewPhotoTile>(Keyboard.FocusedElement).Member?.OccurrenceId);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void QueuedRestoreDoesNotStealNewerDeliberateFocusFromReviewHeading()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready();
            OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel, 960, 720);
            try
            {
                ReviewTiles.Focus(ReviewTiles.Tile(window, 1));

                fixture.Workflow.Publish(ReviewData.WithSelection(
                    fixture.Workflow.Current.Review, [1], 10));
                var deliberate = Assert.IsType<TextBlock>(window.FindName("ReviewHeading"));
                ReviewTiles.Focus(deliberate);

                Drain();

                Assert.True(deliberate.IsKeyboardFocused);
                Assert.Same(deliberate, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void QueuedRestoreDoesNotCrossAReplacementReviewSessionWithTheSameGroupIds()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready();
            OpenIdenticalCopies(fixture.ViewModel);
            var window = ReviewTiles.Show(fixture.ViewModel, 960, 720);
            try
            {
                var original = ReviewTiles.Tile(window, 1);
                ReviewTiles.Focus(original);

                var current = fixture.Workflow.Current.Review;
                var replacement = current with
                {
                    Pin = new ReviewSessionPin(Guid.Parse("21000000-0000-0000-0000-000000000021"), current.Pin!.Scan),
                    SelectionRevision = 1,
                };
                fixture.Workflow.Publish(replacement);
                Drain();

                var replacementTile = ReviewTiles.Tile(window, 1);
                Assert.NotSame(original, replacementTile);
                Assert.False(replacementTile.IsKeyboardFocused);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void UnavailableHighLinkEvidenceNeverClaimsAnExternalSurvivor()
    {
        var fixture = ReviewShellFixture.Ready(review: ReviewData.ExternalUnavailableSnapshot());

        var text = Assert.Single(fixture.ViewModel.ExactGroups).SurvivorText;

        Assert.DoesNotContain("external", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("in-Scan", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContradictoryHighLinkEvidenceIsReportedAsInconsistentNotReassuring()
    {
        var fixture = ReviewShellFixture.Ready(review: ReviewData.InconsistentExternalSnapshot());

        var text = Assert.Single(fixture.ViewModel.ExactGroups).SurvivorText;

        Assert.DoesNotContain("external", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inconsistent", text, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSelection(SelectionCommand command, ExactGroupViewModel group,
        SelectionCommandKind kind, long[] ids)
        => AssertSelection(command, group, group.SelectionRevision, kind, ids);

    private static void AssertSelection(SelectionCommand command, ExactGroupViewModel group,
        long selectionRevision, SelectionCommandKind kind, long[] ids)
    {
        Assert.Equal(group.GroupId, command.Pin.GroupId);
        Assert.Equal(group.GroupRevision, command.Pin.GroupRevision);
        Assert.Equal(selectionRevision, command.Pin.SelectionRevision);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(ids, command.OccurrenceIds.ToArray());
        Assert.NotEqual(Guid.Empty, command.CommandId);
    }

    private static void InvokeReviewPhase(
        MainWindow window,
        ShellViewModel viewModel,
        ManualWorkflowScheduler scheduler)
    {
        window.UpdateLayout();
        var button = Descendants<Button>(window)
            .Single(control => control.DataContext is PhaseItemViewModel { Phase: WorkflowPhase.Review });
        Assert.True(button.IsVisible);
        Assert.True(button.IsEnabled);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        Assert.NotNull(peer);
        var command = viewModel.NavigateCommand;
        var started = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveCommandExecution(object? sender, EventArgs eventArgs)
        {
            if (!command.CanExecute(WorkflowPhase.Review)) started = true;
            else if (started) completed.TrySetResult();
        }
        command.CanExecuteChanged += ObserveCommandExecution;
        try
        {
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            PumpWorkflowUntil(() => completed.Task.IsCompleted, scheduler);
            completed.Task.GetAwaiter().GetResult();
            Assert.True(started, "The real Review phase button did not begin its bound command.");
        }
        finally
        {
            command.CanExecuteChanged -= ObserveCommandExecution;
        }
    }

    internal static void FinishOnSta(Task task, ManualWorkflowScheduler scheduler)
    {
        PumpWorkflowUntil(() => task.IsCompleted, scheduler);
        task.GetAwaiter().GetResult();
        scheduler.Drain();
        Drain();
    }

    private static void PumpWorkflowUntil(Func<bool> condition, ManualWorkflowScheduler scheduler)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
        {
            scheduler.Drain();
            Drain();
        }
        Assert.True(condition(), "The real workflow did not settle before the two-second deadline.");
    }

    private static ListBoxItem RealizeContainer(ListBox list, object item, bool drain = true)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            list.ScrollIntoView(item);
            list.UpdateLayout();
            if (drain) Drain();
            list.ApplyTemplate();
            if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                return container;
        }
        throw new InvalidOperationException("The focused Review group was not realized after five layout turns.");
    }

    internal static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Dispatcher? dispatcher = null;
        using var dispatcherReady = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcherReady.Set();
            try
            {
                action();
                Drain();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                try
                {
                    Drain();
                    if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
                }
                catch (Exception error)
                {
                    failure ??= ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        }) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(dispatcherReady.Wait(TimeSpan.FromSeconds(2)), "The Review STA dispatcher did not start.");
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                Environment.FailFast("The Review STA survived bounded Dispatcher shutdown.");
            throw new TimeoutException("Review WPF scenario exceeded fifteen seconds and its dispatcher was shut down.");
        }
        failure?.Throw();
    }

    internal static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // Opens Identical copies, so the gallery shows its sets.
    internal static void OpenIdenticalCopies(ShellViewModel viewModel)
    {
        var exact = viewModel.ReviewCategories.Single(
            category => category.Category == ReviewCategoryKind.Exact);
        viewModel.OpenReviewCategoryCommand.ExecuteAsync(exact).GetAwaiter().GetResult();
    }

    internal static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target);
        Assert.NotNull(source);
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key);
        down.RoutedEvent = Keyboard.PreviewKeyDownEvent;
        down.Source = target;
        Assert.Same(target, down.OriginalSource);
        target.RaiseEvent(down);
        if (!down.Handled && target.IsVisible)
        {
            down.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(down);
        }
        if (!target.IsVisible) return;
        var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key);
        up.RoutedEvent = Keyboard.PreviewKeyUpEvent;
        up.Source = target;
        Assert.Same(target, up.OriginalSource);
        target.RaiseEvent(up);
        if (!up.Handled && target.IsVisible)
        {
            up.RoutedEvent = Keyboard.KeyUpEvent;
            target.RaiseEvent(up);
        }
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}

internal sealed class ReviewShellFixture
{
    private ReviewShellFixture(bool twoGroups, bool externalSurvivor, ImmutableArray<long> selected,
        IReviewDialogService? dialog, ReviewSnapshot? review)
    {
        Scope = new ReviewStaticScope();
        Workflow = new RecordingReviewWorkflow(Scope.Current,
            review ?? ReviewData.CreateSnapshot(twoGroups, externalSurvivor, selected));
        Dialog = dialog ?? new RecordingReviewDialogService();
        ViewModel = new ShellViewModel(Scope, Workflow, Dialog);
    }

    public ReviewStaticScope Scope { get; }
    public RecordingReviewWorkflow Workflow { get; }
    public IReviewDialogService Dialog { get; }
    public ShellViewModel ViewModel { get; }

    public static ReviewShellFixture Ready(bool twoGroups = false, bool externalSurvivor = false,
        ImmutableArray<long> selected = default, IReviewDialogService? dialog = null, ReviewSnapshot? review = null) =>
        new(twoGroups, externalSurvivor, selected.IsDefault ? [] : selected, dialog, review);
}

internal sealed class ReviewStaticScope : IChooseScopeWorkflow
{
    public ShellSnapshot Current { get; } = new(InitialWorkflow.Create(), "Ready", ScopeSessionState.Ready,
        new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), Revision: 7)
    {
        Preflight = new ScopePreflightResult(
            ScopePreflightDecision.Ready,
            new ResolvedScope(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"\\?\C:\Photos",
                new DirectoryIdentity(7, 0, 70)),
            ScopeVolumeKind.Fixed,
            new ScopeOutcomeCounts(4, 0, 0, 0),
            new ScopeRestrictionCounts(0, 0),
            []),
    };
    public event EventHandler<ShellSnapshot>? SnapshotChanged { add { } remove { } }
    public Task HandleAsync(ChooseScopeCommand command, CancellationToken token) => Task.CompletedTask;
    public Task HandleAsync(ChangeScopeRecursionCommand command, CancellationToken token) => Task.CompletedTask;
    public Task HandleAsync(RetryScopePreflightCommand command, CancellationToken token) => Task.CompletedTask;
}

internal sealed class RecordingReviewWorkflow : IAlbumenWorkflow
{
    private readonly Queue<ReviewCommandResult> selectionResults = new();
    private TaskCompletionSource<ReviewCommandResult>? deferredSelection;
    private TaskCompletionSource<ReviewCommandResult>? deferredAffirmation;

    public RecordingReviewWorkflow(ShellSnapshot scope, ReviewSnapshot review)
    {
        var groups = review.Groups.Select(group => group.Candidate).ToImmutableArray();
        var rows = groups.SelectMany(group => group.Members).ToImmutableArray();
        var scan = new ScanSnapshot(review.Pin!.Scan, review.Coverage,
            new(ScanStageState.Succeeded, ScanReason.None), rows, groups,
            new(rows.Length, rows.Length, 0, 0, 0, 0));
        Current = new AlbumenSnapshot(scope, new(scope.Revision, review.Pin.Scan.RuleSetRevision), scan,
            WorkflowPhase.Review, InitialWorkflow.Create()) { Review = review };
    }

    public AlbumenSnapshot Current { get; private set; }
    public List<SelectionCommand> SelectionCommands { get; } = [];
    public List<OverrideAffirmationCommand> Affirmations { get; } = [];
    public List<ApproveSelectionCommand> Approvals { get; } = [];
    public int OverrideEvidenceReadAttempts { get; private set; }
    public ReviewCommandResult NextSelectionResult { set => selectionResults.Enqueue(value); }
    public event EventHandler<AlbumenSnapshot>? SnapshotChanged;

    public Task ContinueAsync() => Task.CompletedTask;
    public Task SetScanOptionsAsync(ScanOptions options) => Task.CompletedTask;
    public Task ConfirmExactRulesAsync() => Task.CompletedTask;
    public Task StartScanAsync() => Task.CompletedTask;
    public Task CancelScanAsync() => Task.CompletedTask;
    public Task OpenReviewAsync() => Task.CompletedTask;
    public Task RevalidateUnknownAsync(Guid requestId, CancellationToken token) => Task.CompletedTask;
    public Task NavigateAsync(WorkflowPhase phase) => Task.CompletedTask;
    public Task ContinueReviewAsync() => Task.CompletedTask;

    public TaskCompletionSource<ReviewCommandResult> DeferNextSelection() =>
        deferredSelection = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<ReviewCommandResult> DeferNextAffirmation() =>
        deferredAffirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ReviewCommandResult> ChangeReviewSelectionAsync(SelectionCommand command, CancellationToken token)
    {
        SelectionCommands.Add(command);
        if (Interlocked.Exchange(ref deferredSelection, null) is { } deferred)
            return await deferred.Task;
        var result = selectionResults.Count > 0 ? selectionResults.Dequeue() : CommitSelection(command);
        Publish(result.Snapshot);
        return result;
    }

    public async Task<ReviewCommandResult> AffirmReviewOverrideAsync(OverrideAffirmationCommand command, CancellationToken token)
    {
        Affirmations.Add(command);
        if (Interlocked.Exchange(ref deferredAffirmation, null) is { } deferred)
            return await deferred.Task;
        return new ReviewCommandResult(ReviewCommandOutcome.Committed, Current.Review);
    }

    public Task<ReviewCommandResult> ApproveReviewAsync(ApproveSelectionCommand command, CancellationToken token)
    {
        Approvals.Add(command);
        OverrideEvidenceReadAttempts++;
        return Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Approved, Current.Review));
    }

    public void Publish(ReviewSnapshot review)
    {
        Current = Current with { Review = review };
        SnapshotChanged?.Invoke(this, Current);
    }

    private ReviewCommandResult CommitSelection(SelectionCommand command)
    {
        var selected = command.Kind == SelectionCommandKind.Toggle
            ? Current.Review.Groups.Single(group => group.Candidate.GroupId == command.Pin.GroupId)
                .SelectedOccurrenceIds.SymmetricExcept(command.OccurrenceIds)
            : command.OccurrenceIds.ToImmutableHashSet();
        var groups = Current.Review.Groups.Select(group => group.Candidate.GroupId != command.Pin.GroupId ? group :
            group with
            {
                SelectedOccurrenceIds = selected,
                Members = group.Members.Select(member => member with
                    { Selected = selected.Contains(member.Occurrence.Token.OccurrenceId) }).ToImmutableArray(),
                Recovery = new SelectionRecovery(selected.Count == 0 ? 0 : 512, SelectionRecoveryUnavailableReason.None),
            }).ToImmutableArray();
        var review = Current.Review with { SelectionRevision = checked(Current.Review.SelectionRevision + 1), Groups = groups };
        return new ReviewCommandResult(ReviewCommandOutcome.Committed, review);
    }
}

internal sealed class RecordingReviewDialogService : IReviewDialogService
{
    public SurvivorOverrideDialogResult Result { get; set; } = SurvivorOverrideDialogResult.Cancel;
    public List<OverrideProposalToken> Proposals { get; } = [];
    public List<IInputElement> InvokingFocus { get; } = [];
    public Task<SurvivorOverrideDialogResult> ShowSurvivorOverrideAsync(OverrideProposalToken proposal,
        IInputElement invokingFocus, CancellationToken cancellationToken)
    {
        Proposals.Add(proposal);
        InvokingFocus.Add(invokingFocus);
        return Task.FromResult(Result);
    }
}

internal static class ReviewData
{
    internal static ReviewSnapshot WithSelection(
        ReviewSnapshot snapshot, ImmutableArray<long> selected, long selectionRevision)
    {
        var selectedSet = selected.ToImmutableHashSet();
        var group = snapshot.Groups[0];
        var members = group.Members.Select(member => member with
            { Selected = selectedSet.Contains(member.Occurrence.Token.OccurrenceId) }).ToImmutableArray();
        return snapshot with
        {
            SelectionRevision = selectionRevision,
            Groups = snapshot.Groups.SetItem(0, group with
            {
                Members = members,
                SelectedOccurrenceIds = selectedSet,
                Recovery = new(selected.Length * 512, SelectionRecoveryUnavailableReason.None),
            }),
        };
    }

    internal static ReviewSnapshot ExternalUnavailableSnapshot()
    {
        var snapshot = CreateSnapshot(externalSurvivor: true);
        var group = snapshot.Groups[0];
        return snapshot with { Groups = snapshot.Groups.SetItem(0, group with
        {
            Members = group.Members.SetItem(0, group.Members[0] with
                { Eligibility = ReviewEligibilityState.Unavailable, Reason = ReviewUnavailableReason.Missing }),
        }) };
    }

    internal static ReviewSnapshot InconsistentExternalSnapshot()
    {
        var snapshot = CreateSnapshot();
        var group = snapshot.Groups[0];
        var first = group.Members[0].Occurrence;
        var fourth = group.Members[3].Occurrence;
        var key = first.PhysicalFileKey!.Value;
        first = new(first.Token, first.DiscoveredPath, first.ResolvedPath, first.PhysicalFileKey,
            first.Enumerate, first.Exact, first.Content, new(512, 3, true));
        fourth = new(fourth.Token, fourth.DiscoveredPath, fourth.ResolvedPath, key,
            fourth.Enumerate, fourth.Exact, new(key, fourth.Content!.ByteLength, fourth.Content.Sha256),
            new(512, 1, true));
        var candidateMembers = group.Candidate.Members.SetItem(0, first).SetItem(3, fourth);
        var reviewMembers = group.Members
            .SetItem(0, group.Members[0] with { Occurrence = first })
            .SetItem(3, group.Members[3] with { Occurrence = fourth });
        return snapshot with { Groups = snapshot.Groups.SetItem(0, group with
        {
            Candidate = group.Candidate with { Members = candidateMembers },
            Members = reviewMembers,
        }) };
    }

    internal static ReviewSnapshot CreateSnapshot(bool twoGroups = false, bool externalSurvivor = false,
        ImmutableArray<long> selected = default)
    {
        selected = selected.IsDefault ? [] : selected;
        var scan = new ScanPin(Guid.Parse("10000000-0000-0000-0000-000000000001"), 7, 3, 5);
        var session = new ReviewSessionPin(Guid.Parse("20000000-0000-0000-0000-000000000002"), scan);
        var groups = ImmutableArray.Create(Group(scan, Guid.Parse("30000000-0000-0000-0000-000000000003"), 0,
            externalSurvivor, selected));
        if (twoGroups)
            groups = groups.Add(Group(scan, Guid.Parse("40000000-0000-0000-0000-000000000004"), 10,
                false, []));
        return new ReviewSnapshot(session, ReviewStatus.Ready, ScanStatus.Incomplete, 9, groups,
            new(1, 2, 3, 4, 1, 1, 2), false, null, ReviewUnavailableReason.None);
    }

    internal static ReviewSnapshot CreateLargeMemberSnapshot(int memberCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memberCount, 2);
        var snapshot = CreateSnapshot();
        var original = snapshot.Groups[0];
        var rows = Enumerable.Range(1, memberCount)
            .Select(index => Row(snapshot.Pin!.Scan, index, checked((ulong)index),
                $@"C:\Photos\large-{index}.jpg", 1))
            .ToImmutableArray();
        var candidate = original.Candidate with { Members = rows };
        var members = rows.Select(row => new ReviewMemberSnapshot(
                row, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None, false))
            .ToImmutableArray();
        return snapshot with
        {
            Groups = [original with
            {
                Candidate = candidate,
                Members = members,
                SelectedOccurrenceIds = [],
                Recovery = new(0, SelectionRecoveryUnavailableReason.None),
            }],
        };
    }

    internal static OverrideProposalToken Proposal(ReviewSnapshot snapshot) => new(
        Guid.Parse("50000000-0000-0000-0000-000000000005"),
        new(snapshot.Pin!, snapshot.Groups[0].Candidate.GroupId, snapshot.Groups[0].Candidate.GroupRevision,
            snapshot.SelectionRevision), [1, 4], SurvivorPolicy.Version, "consequence-v1");

    private static ReviewGroupSnapshot Group(ScanPin scan, Guid id, long offset, bool externalSurvivor,
        ImmutableArray<long> selected)
    {
        var first = Row(scan, offset + 1, (ulong)(offset + 1), @"C:\Photos\照片-very-long-name-one.jpg",
            externalSurvivor ? 2u : 1u);
        var pending = Row(scan, offset + 2, (ulong)(offset + 2), @"C:\Photos\pending.jpg", 1);
        var unavailable = Row(scan, offset + 3, (ulong)(offset + 3), @"C:\Photos\changed.jpg", 1);
        var fourth = Row(scan, offset + 4, (ulong)(offset + 4), @"C:\Photos\fourth.jpg", 1);
        var candidate = new CandidateGroup(id, 4, scan, CandidateCategory.Exact,
            [first, pending, unavailable, fourth], [], false, null, new(2048, RecoveryUnavailableReason.None));
        var members = ImmutableArray.Create(
            new ReviewMemberSnapshot(first, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None,
                selected.Contains(first.Token.OccurrenceId)),
            new ReviewMemberSnapshot(pending, ReviewEligibilityState.PendingRevalidation, ReviewUnavailableReason.None,
                selected.Contains(pending.Token.OccurrenceId)),
            new ReviewMemberSnapshot(unavailable, ReviewEligibilityState.Unavailable, ReviewUnavailableReason.Changed,
                selected.Contains(unavailable.Token.OccurrenceId)),
            new ReviewMemberSnapshot(fourth, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None,
                selected.Contains(fourth.Token.OccurrenceId)));
        return new ReviewGroupSnapshot(candidate, members, selected.ToImmutableHashSet(),
            new SelectionRecovery(externalSurvivor ? 0 : selected.Length * 512,
                SelectionRecoveryUnavailableReason.None), false, null);
    }

    private static ScanFileOccurrence Row(ScanPin scan, long id, ulong physical, string path, uint links)
    {
        var key = new PhysicalFileKey(7, new(0, physical));
        return new(new(scan, id, 2, 3), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 512, new string('A', 64)), new(512, links, true));
    }
}
