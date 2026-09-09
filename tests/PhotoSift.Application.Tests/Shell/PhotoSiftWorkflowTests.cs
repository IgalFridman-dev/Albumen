using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Application.Tests.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Application.Tests.Shell;

public sealed class PhotoSiftWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewReplacementAndInvalidationAdapterFailuresAreSupervised(bool invalidate)
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var review = new AdmissionBarrierReview(f.Owner, blockFirst: false);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Run();
        var original = f.Owner.Current.Pin;
        var failure = new InvalidOperationException("Review adapter failure");
        if (invalidate) review.InvalidationOverride = _ => Task.FromException(failure);
        else review.OpenOverride = _ => Task.FromException<ReviewCommandResult>(failure);
        var faulted = ScanTestData.Signal<bool>();
        f.Owner.SnapshotChanged += (_, snapshot) =>
        { if (snapshot.Status == ReviewStatus.Faulted) faulted.TrySetResult(true); };
        var capture = new CapturedAsyncExceptions();
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(capture);
        try
        {
            scan.Publish(invalidate
                ? f.Request.Scan with { Pin = f.Request.Scan.Pin! with { ScanId = Guid.NewGuid() } }
                : f.Request.Scan with { Groups = [f.Request.Scan.Groups[0] with { GroupRevision = 2 }] });
        }
        finally { SynchronizationContext.SetSynchronizationContext(before); }
        await PumpCaptured(f.Scheduler, Task.WhenAny(faulted.Task, capture.Escaped.Task), capture);
        Assert.False(capture.Escaped.Task.IsCompleted);
        Assert.Equal(original, f.Owner.Current.Pin);
        Assert.Equal(ReviewStatus.Faulted, f.Owner.Current.Status);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PumpCaptured(f.Scheduler, workflow.NavigateAsync(WorkflowPhase.Scan), capture));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task SubscriberQueuedReplacementCannotBePoisonedByTheOlderProjectionFault()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Run();
        var old = f.Owner.Current.Pin;
        Task<ReviewCommandResult>? replacement = null;
        var invoked = ScanTestData.Signal<bool>();
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (replacement is not null || snapshot.Review.Unresolved.PendingReviewMembers != 2) return;
            replacement = f.Owner.OpenAsync(f.Request, TestContext.Current.CancellationToken);
            invoked.TrySetResult(true);
            throw new InvalidOperationException("old presentation failure");
        };
        var capture = new CapturedAsyncExceptions();
        await PumpCaptured(f.Scheduler, f.Owner.CommitRevalidationAsync(f.Evidence(1)).AsTask(), capture);
        await PumpCaptured(f.Scheduler, invoked.Task, capture);
        await PumpCaptured(f.Scheduler, replacement!, capture);
        // A command queued after the report is a deterministic owner barrier.
        await PumpCaptured(f.Scheduler, f.Owner.ReportObserverFaultAsync(old!), capture);
        Assert.False(capture.Escaped.Task.IsCompleted);
        Assert.NotEqual(old, f.Owner.Current.Pin);
        Assert.Equal(ReviewStatus.Revalidating, f.Owner.Current.Status);
        Assert.Equal(ReviewUnavailableReason.None, f.Owner.Current.FaultReason);
    }

    [Fact]
    public async Task HistoricalSamePinScanAndReviewEventsCannotOverwriteCurrentOwnerFacts()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var review = new AdmissionBarrierReview(f.Owner, blockFirst: false);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var old = f.Owner.Current;
        var newerScan = f.Request.Scan with { Groups = [f.Request.Scan.Groups[0] with { GroupRevision = 2 }] };
        var replaced = ScanTestData.Signal<bool>();
        f.Owner.SnapshotChanged += (_, snapshot) =>
        { if (snapshot.Groups.Any(g => g.Candidate.GroupRevision == 2)) replaced.TrySetResult(true); };
        scan.Publish(newerScan);
        await f.Pump(replaced.Task);
        var latest = f.Owner.Current;
        review.Replay(latest);
        review.Replay(old);
        review.Replay(ReviewSnapshot.Closed);
        scan.Replay(f.Request.Scan);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Review));
        Assert.Same(newerScan, workflow.Current.Scan);
        Assert.Same(latest, workflow.Current.Review);
        Assert.Empty(workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
    }

    [Fact]
    public async Task ThrowingReviewProjectionSubscriberFaultsTheExactOwnerWithoutEscapingAsyncVoid()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Run();
        var failure = new InvalidOperationException("presentation subscriber");
        var threw = false;
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (!threw && snapshot.Review.Unresolved.PendingReviewMembers == 2)
            { threw = true; throw failure; }
        };
        var faulted = ScanTestData.Signal<bool>();
        f.Owner.SnapshotChanged += (_, snapshot) =>
        { if (snapshot.Status == ReviewStatus.Faulted) faulted.TrySetResult(true); };
        var capture = new CapturedAsyncExceptions();
        var ack = f.Owner.CommitRevalidationAsync(f.Evidence(1)).AsTask();
        await PumpCaptured(f.Scheduler, ack, capture);
        await PumpCaptured(f.Scheduler, Task.WhenAny(faulted.Task, capture.Escaped.Task), capture);
        Assert.False(capture.Escaped.Task.IsCompleted, "Workflow adapter exceptions must be observed, not posted as unhandled async-void failures.");
        Assert.True(faulted.Task.IsCompletedSuccessfully);
        Assert.Equal(ReviewStatus.Faulted, f.Owner.Current.Status);
        var next = workflow.NavigateAsync(WorkflowPhase.Scan);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PumpCaptured(f.Scheduler, next, capture));
        Assert.Same(failure, error);
    }

    [Fact]
    public async Task ScopeInvalidationFailureWithoutLiveReviewIsRetainedForTheNextWorkflowCommand()
    {
        var scheduler = new ManualScanScheduler();
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope);
        var failure = new InvalidOperationException("invalidation adapter");
        var review = new DeferredReview { InvalidationFailure = failure };
        var workflow = new PhotoSiftWorkflow(scope, new ScanOwner(), scheduler, review);
        var capture = new CapturedAsyncExceptions();
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(capture);
        try { scope.Publish(f.Request.Scope with { Revision = 2 }); }
        finally { SynchronizationContext.SetSynchronizationContext(before); }
        await PumpCaptured(scheduler, review.Invalidated.Task, capture);
        var next = workflow.NavigateAsync(WorkflowPhase.ChooseScope);
        Exception? observed = null;
        try { await PumpCaptured(scheduler, next, capture); }
        catch (InvalidOperationException exception) { observed = exception; }
        Assert.Same(failure, observed);
        Assert.False(capture.Escaped.Task.IsCompleted);
        Assert.Null(workflow.Current.Review.Pin);
    }

    [Fact]
    public async Task CapturedOldCandidateCannotNavigateReviewAfterSamePinReplacementBeforeAdmission()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var review = new AdmissionBarrierReview(f.Owner);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        var navigatedOld = false;
        workflow.SnapshotChanged += (_, snapshot) =>
            navigatedOld |= snapshot.CurrentPhase == WorkflowPhase.Review
                && snapshot.Review.Groups.Any(g => g.Candidate.GroupRevision == 1);
        var opening = workflow.OpenReviewAsync();
        try
        {
            await f.Pump(review.Captured.Task);
            Assert.Same(f.Request.Scan, (await review.Captured.Task).Scan);
            var newer = f.Request.Scan with { Groups = [f.Request.Scan.Groups[0] with { GroupRevision = 2 }] };
            scan.Publish(newer);
            await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
            review.Release.TrySetResult(true);
            await f.Pump(opening);
            Assert.False(navigatedOld, "A captured older candidate must never navigate as the current Review.");
            Assert.Equal(2, Assert.Single(f.Owner.Current.Groups).Candidate.GroupRevision);
            Assert.Same(f.Owner.Current, workflow.Current.Review);
            Assert.Empty(workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
            Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        }
        finally
        {
            review.Release.TrySetResult(true);
            await f.Pump(opening);
        }
    }

    [Fact]
    public async Task SameScanNewerCandidateReplacesReviewDirectlyWithEmptySelection()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var previous = f.Owner.Current;
        var stale = f.Command(SelectionCommandKind.Toggle, 2);
        var replacement = ScanTestData.Signal<bool>();
        var observedClosed = false;
        f.Owner.SnapshotChanged += (_, snapshot) =>
        {
            observedClosed |= snapshot.Status == ReviewStatus.Closed;
            if (snapshot.Groups.Any(g => g.Candidate.GroupRevision == 2)) replacement.TrySetResult(true);
        };
        scan.Publish(f.Request.Scan with { Groups = [f.Request.Scan.Groups[0] with { GroupRevision = 2 }] });
        try { await f.Pump(replacement.Task); }
        catch (TimeoutException) { }
        Assert.True(replacement.Task.IsCompletedSuccessfully, "A newer same-Scan candidate must replace the active Review.");
        Assert.NotEqual(previous.Pin, f.Owner.Current.Pin);
        Assert.Empty(f.Owner.Current.Groups[0].SelectedOccurrenceIds);
        Assert.False(observedClosed);
        Assert.Equal(ReviewCommandOutcome.Stale, (await f.Change(stale)).Outcome);
    }

    [Fact]
    public async Task OpeningReviewCapturesImmutableRequestAndDoesNotHoldWorkflowTurn()
    {
        var scheduler = new ManualScanScheduler();
        await using var data = new ReviewFixture();
        var scope = new ScopeOwner(data.Request.Scope);
        var scan = new ScanOwner();
        var review = new DeferredReview();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler, review);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.ContinueAsync()));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.ConfirmExactRulesAsync()));
        scan.Publish(data.Request.Scan);
        var opening = workflow.OpenReviewAsync();
        await ScanTestData.Bound(scheduler.PumpUntilAsync(review.Entered.Task));
        Assert.Same(data.Request.Scope, review.Request!.Scope);
        Assert.Same(data.Request.Scan, review.Request.Scan);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.NavigateAsync(WorkflowPhase.ChooseScope)));
        Assert.False(opening.IsCompleted);
        scope.Publish(data.Request.Scope with { Revision = 2 });
        Assert.Equal(2, review.Invalidations);
        Assert.Null(review.LastInvalidation);
        var accepted = await data.Open();
        review.Done.SetResult(accepted);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(opening));
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Null(workflow.Current.Review.Pin);
    }

    [Fact]
    public async Task ReviewProjectionPreservesSelectionThroughNavigationAndQueuesScanInvalidation()
    {
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope);
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Review));
        Assert.Same(f.Owner.Current, workflow.Current.Review);
        Assert.Equal([1L], workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
        Assert.Null(workflow.Current.Review.ApprovedRequest);
        scan.Publish(f.Request.Scan with { Pin = f.Request.Scan.Pin! with { ScanId = Guid.NewGuid() }, Status = ScanStatus.Checking });
        var late = f.Owner.ChangeSelectionAsync(f.Command(SelectionCommandKind.Toggle, 2), TestContext.Current.CancellationToken);
        await f.Pump(late);
        Assert.Equal(ReviewCommandOutcome.Stale, (await late).Outcome);
        Assert.Null(f.Owner.Current.Pin);
    }
    [Fact]
    public async Task ContinueAndConfirmationDoNotStartWorkAndStartRequiresExplicitConfirmation()
    {
        var scope = new ScopeOwner(Ready(1));
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, new PostedScheduler());
        Assert.Equal(5, workflow.Current.Phases.Length);
        await workflow.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(scan.Starts);
        await workflow.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.ConfirmRules, workflow.Current.CurrentPhase);
        Assert.Single(workflow.Current.Phases, phase => phase.Availability == PhaseAvailability.Current);
        Assert.Equal(PhaseAvailability.Available, workflow.Current.Phases[0].Availability);
        Assert.Null(workflow.Current.Rules);
        await workflow.ConfirmExactRulesAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(workflow.Current.Rules);
        Assert.Empty(scan.Starts);
        await workflow.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var started = Assert.Single(scan.Starts);
        Assert.Equal(1, started.ScopeRevision);
        Assert.Equal(workflow.Current.Rules, started);
        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
        await workflow.NavigateAsync(WorkflowPhase.ApproveCleanup).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
    }

    [Fact]
    public async Task ColdAndUnconfirmedScopeCannotBypassContinue()
    {
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(new(InitialWorkflow.Create(), "Choose")), new ScanOwner(), new PostedScheduler());
        await workflow.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await workflow.ConfirmExactRulesAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await workflow.NavigateAsync(WorkflowPhase.Review).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Null(workflow.Current.Rules);
        Assert.All(workflow.Current.Phases.Skip(1), p => Assert.Equal(PhaseAvailability.Unavailable, p.Availability));
    }

    [Fact]
    public async Task AcceptedScopeChangeRevokesRulesAndQueuedOldReviewCannotRestoreThem()
    {
        var scheduler = new ManualScheduler();
        var scope = new ScopeOwner(Ready(1));
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var oldRules = workflow.Current.Rules!;
        var oldScan = EmptyScan(new(Guid.NewGuid(), 1, oldRules.RuleSetRevision, 1), ScanStatus.Complete);
        scan.Publish(oldScan); // This presentation result is queued before acceptance changes.
        scope.Publish(Ready(2) with { ScopeState = ScopeSessionState.Checking });
        scheduler.Drain();
        Assert.Null(workflow.Current.Rules);
        Assert.Null(workflow.Current.Scan.Pin);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        scan.Publish(oldScan);
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.Review));
        Assert.Null(workflow.Current.Scan.Pin);
        scope.Publish(Ready(2));
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        Assert.True(workflow.Current.Rules!.RuleSetRevision > oldRules.RuleSetRevision);
    }

    [Fact]
    public async Task NeutralScopePublicationPreservesReviewButFreshRetryRevisionRevokesIt()
    {
        var scheduler = new ManualScheduler();
        var scope = new ScopeOwner(Ready(1));
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var rules = workflow.Current.Rules!;
        scan.Publish(EmptyScan(new(Guid.NewGuid(), 1, rules.RuleSetRevision, 1), ScanStatus.Incomplete));
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.Review));
        scope.Publish(scope.Current);
        scheduler.Drain();
        Assert.Equal(rules, workflow.Current.Rules);
        Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        scope.Publish(Ready(2) with { ScopeState = ScopeSessionState.Checking });
        scheduler.Drain();
        Assert.Null(workflow.Current.Rules);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
    }

    [Fact]
    public async Task CancellationDoesNotHoldShellTurn()
    {
        var scheduler = new ManualScheduler();
        var scan = new ScanOwner { Cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        await scheduler.Finish(workflow.StartScanAsync());
        var cancel = workflow.CancelScanAsync();
        scheduler.Drain();
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        scan.Cancellation.SetResult();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ObserverConfirmationCannotReenterThePublishingTurn()
    {
        var scheduler = new ManualScheduler();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), scheduler);
        Task? confirmation = null;
        var publishing = false;
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            Assert.False(publishing);
            publishing = true;
            if (snapshot.CurrentPhase == WorkflowPhase.ConfirmRules && snapshot.Rules is null)
            {
                confirmation = workflow.ConfirmExactRulesAsync();
                scheduler.Drain(); // Model a modal UI pump inside the observer.
                Assert.False(confirmation.IsCompleted);
                Assert.Null(workflow.Current.Rules);
            }
            publishing = false;
        };
        await scheduler.Finish(workflow.ContinueAsync());
        Assert.NotNull(confirmation);
        await confirmation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(workflow.Current.Rules);
    }

    [Fact]
    public async Task RealScopeOwnerKeepsCanceledPickerAndSameRecursionNeutralThenFreshRetryCanScan()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Preflight.Inspect = (selection, _) => Task.FromResult(fixture.Evidence with
        {
            Scope = fixture.Scope with { Selection = selection },
        });
        var scope = new ChooseScopeWorkflow(new CancelAfterSelectionPicker(fixture.Scope.Selection.SelectedPath), fixture.Preflight, fixture.Scheduler);
        var workflow = new PhotoSiftWorkflow(scope, fixture.Owner, fixture.Scheduler);
        await fixture.Pump(scope.HandleAsync(new ChooseScopeCommand(), TestContext.Current.CancellationToken));
        await fixture.Pump(workflow.ContinueAsync());
        await fixture.Pump(workflow.ConfirmExactRulesAsync());
        var rules = workflow.Current.Rules!;
        await fixture.Pump(scope.HandleAsync(new ChooseScopeCommand(), TestContext.Current.CancellationToken));
        await fixture.Pump(scope.HandleAsync(new ChangeScopeRecursionCommand(false), TestContext.Current.CancellationToken));
        Assert.Equal(rules, workflow.Current.Rules);
        await fixture.Pump(workflow.StartScanAsync());
        var first = await fixture.Run();
        await fixture.Pump(workflow.CancelScanAsync());
        await fixture.Pump(scope.HandleAsync(new RetryScopePreflightCommand(), TestContext.Current.CancellationToken));
        Assert.Null(workflow.Current.Rules);
        Assert.Null(workflow.Current.Scan.Pin);
        await fixture.Pump(workflow.ContinueAsync());
        await fixture.Pump(workflow.ConfirmExactRulesAsync());
        fixture.Executor.Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Pump(workflow.StartScanAsync());
        var second = await fixture.Run();
        Assert.Equal(2, second.Context.Pin.ScopeRevision);
        Assert.True(second.Context.Pin.RuleSetRevision > first.Context.Pin.RuleSetRevision);
        Assert.NotEqual(first.Context.Pin.ScanId, second.Context.Pin.ScanId);
    }

    [Fact]
    public void RuleConfirmationBelongsToOneReadyAcceptedScopeRevision()
    {
        var scope = Ready(4);
        Assert.False(ShellPhasePolicy.CanStart(scope, null));
        Assert.False(ShellPhasePolicy.CanStart(scope, new(3, 1)));
        Assert.True(ShellPhasePolicy.CanStart(scope, new(4, 1)));
        Assert.False(ShellPhasePolicy.CanStart(scope with { ScopeState = ScopeSessionState.Checking }, new(4, 1)));
    }

    private static ShellSnapshot Ready(long revision) => new(InitialWorkflow.Create(), "Ready", ScopeSessionState.Ready, Revision: revision);
    private static ScanSnapshot EmptyScan(ScanPin? pin = null, ScanStatus status = ScanStatus.Idle) => new(pin, status,
        new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0));
    private sealed class CancelAfterSelectionPicker(string path) : ILocalFolderPicker
    {
        private bool selected;
        public Task<string?> PickSingleAsync(CancellationToken token)
        {
            var result = selected ? null : path;
            selected = true;
            return Task.FromResult(result);
        }
    }
    private sealed class PostedScheduler : IWorkflowScheduler { public void Post(Action action) => ThreadPool.QueueUserWorkItem(_ => action()); }
    private static Task PumpCaptured(ManualScanScheduler scheduler, Task completion, CapturedAsyncExceptions capture) =>
        ScanTestData.Bound(PumpCore(scheduler, completion, capture));

    private static async Task PumpCore(ManualScanScheduler scheduler, Task completion, CapturedAsyncExceptions capture)
    {
        while (!completion.IsCompleted)
        {
            var before = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(capture);
            try { scheduler.TryRunOne(); }
            finally { SynchronizationContext.SetSynchronizationContext(before); }
            await Task.Yield();
        }
        await completion;
    }

    private sealed class CapturedAsyncExceptions : SynchronizationContext
    {
        internal TaskCompletionSource<Exception> Escaped { get; } = ScanTestData.Signal<Exception>();
        public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            try { callback(state); }
            catch (Exception exception) { Escaped.TrySetResult(exception); }
        });
    }
    private sealed class AdmissionBarrierReview(IReviewCoordinator owner, bool blockFirst = true) : IReviewCoordinator
    {
        private int opens;
        private event EventHandler<ReviewSnapshot>? replayObservers;
        internal TaskCompletionSource<ReviewOpenRequest> Captured { get; } = ScanTestData.Signal<ReviewOpenRequest>();
        internal TaskCompletionSource<bool> Release { get; } = ScanTestData.Signal<bool>();
        internal Func<ReviewOpenRequest, Task<ReviewCommandResult>>? OpenOverride { get; set; }
        internal Func<ScanPin?, Task>? InvalidationOverride { get; set; }
        public ReviewSnapshot Current => owner.Current;
        public event EventHandler<ReviewSnapshot>? SnapshotChanged
        {
            add { replayObservers += value; owner.SnapshotChanged += value; }
            remove { replayObservers -= value; owner.SnapshotChanged -= value; }
        }
        internal void Replay(ReviewSnapshot snapshot) => replayObservers?.Invoke(this, snapshot);
        public async Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken)
        {
            if (OpenOverride is not null) return await OpenOverride(request);
            if (blockFirst && Interlocked.Increment(ref opens) == 1)
            {
                Captured.TrySetResult(request);
                await Release.Task;
            }
            return await owner.OpenAsync(request, cancellationToken);
        }
        public Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken cancellationToken) => owner.ChangeSelectionAsync(command, cancellationToken);
        public Task InvalidateAsync(ScanPin? currentScan) => InvalidationOverride?.Invoke(currentScan) ?? owner.InvalidateAsync(currentScan);
        public Task ReportObserverFaultAsync(ReviewSessionPin session) => owner.ReportObserverFaultAsync(session);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class DeferredReview : IReviewCoordinator
    {
        public ReviewSnapshot Current => ReviewSnapshot.Closed;
        public event EventHandler<ReviewSnapshot>? SnapshotChanged { add { } remove { } }
        internal TaskCompletionSource<bool> Entered { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<ReviewCommandResult> Done { get; } = ScanTestData.Signal<ReviewCommandResult>();
        internal ReviewOpenRequest? Request { get; private set; }
        internal int Invalidations { get; private set; }
        internal ScanPin? LastInvalidation { get; private set; }
        internal Exception? InvalidationFailure { get; init; }
        internal TaskCompletionSource<bool> Invalidated { get; } = ScanTestData.Signal<bool>();
        public Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken)
        { Request = request; Entered.TrySetResult(true); return Done.Task; }
        public Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InvalidateAsync(ScanPin? currentScan)
        {
            Invalidations++;
            LastInvalidation = currentScan;
            Invalidated.TrySetResult(true);
            return InvalidationFailure is null ? Task.CompletedTask : Task.FromException(InvalidationFailure);
        }
        public Task ReportObserverFaultAsync(ReviewSessionPin session) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ManualScheduler : IWorkflowScheduler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();
        public void Post(Action action) => queue.Enqueue(action);
        public void Drain() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Finish(Task task) { Drain(); await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); Drain(); }
    }
    private sealed class ScopeOwner(ShellSnapshot current) : IChooseScopeWorkflow
    {
        public ShellSnapshot Current { get; private set; } = current;
        public event EventHandler<ShellSnapshot>? SnapshotChanged;
        public void Publish(ShellSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        public Task HandleAsync(ChooseScopeCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(ChangeScopeRecursionCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(RetryScopePreflightCommand command, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ScanOwner : IScanCoordinator
    {
        public ScanSnapshot Current { get; private set; } = EmptyScan();
        public List<ExactRuleConfirmation> Starts { get; } = [];
        public TaskCompletionSource? Cancellation { get; init; }
        public event EventHandler<ScanSnapshot>? SnapshotChanged;
        public void Publish(ScanSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        internal void Replay(ScanSnapshot snapshot) => SnapshotChanged?.Invoke(this, snapshot);
        public Task StartAsync(ShellSnapshot scope, ExactRuleConfirmation rules, CancellationToken token) { Starts.Add(rules); return Task.CompletedTask; }
        public Task InvalidateAsync(long scopeRevision, long? ruleRevision) => Task.CompletedTask;
        public Task CancelAsync() => Cancellation?.Task ?? Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}





