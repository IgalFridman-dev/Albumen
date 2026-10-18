using System.Collections.Immutable;
using PhotoSift.Application.Action;
using PhotoSift.Application.Tests.ActionExecution;
using PhotoSift.Domain.Action;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Application.Tests.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Application.Tests.Shell;

public sealed class PhotoSiftWorkflowTests
{
    [Fact]
    public async Task ExplicitApprovalForwardsTheOwnersExactSealedRequestAndWaitsForActionAdmissionToNavigate()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var action = new DeferredAction();
        var scope = new ScopeOwner(f.Request.Scope);
        var workflow = CreateActionWorkflow(scope, scan, f.Scheduler, f.Owner, action);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var command = new ApproveSelectionCommand(f.Owner.Current.Pin!, f.Owner.Current.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var approving = workflow.ApproveReviewAsync(command, TestContext.Current.CancellationToken);
        try
        {
            await f.Pump(action.Entered.Task);
            var request = Assert.Single(action.Requests);
            Assert.Same(f.Owner.Current.ApprovedRequest, request);
            Assert.Equal(command.IdempotencyKey, request.RequestId);
            // Review's accepted seal advances the Selection revision once.
            // Forward the sealed revision, never the pre-seal button revision.
            Assert.Equal(command.SelectionRevision + 1, request.SelectionRevision);
            Assert.Equal(f.Owner.Current.SelectionRevision, request.SelectionRevision);
            Assert.Equal([1L], request.SelectedOccurrenceIds);
            Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
            Assert.Equal(ActionStatus.Idle, ProjectedAction(workflow).Status);
            Assert.Same(ReviewSnapshot.Closed, ProjectedActionReview(workflow));
            var sealedReview = f.Owner.Current;
            var admitted = new ActionSnapshot(request.RequestId, ActionStatus.Claiming,
                [new(request.Items[0], null, ActionItemState.Waiting, ActionSafetyReason.None)], 0, 0, 0, 0, 0, ActionSafetyReason.None);
            action.Publish(admitted);
            // No inline ViewModel/workflow publication is permitted on the Action callback.
            Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
            await f.Pump(workflow.NavigateAsync(WorkflowPhase.ApproveCleanup));
            Assert.Equal(WorkflowPhase.ApproveCleanup, workflow.Current.CurrentPhase);
            Assert.Same(admitted, ProjectedAction(workflow));
            Assert.Same(sealedReview, ProjectedActionReview(workflow));
            Assert.Equal(admitted.RequestId, ProjectedActionReview(workflow).ApprovedRequest!.RequestId);
            Assert.True(workflow.Current.Review.Sealed);
            Assert.Same(request, workflow.Current.Review.ApprovedRequest);
            Assert.False(approving.IsCompleted);
            action.Completion.TrySetResult(new(ActionCommandOutcome.Completed, admitted));
            await f.Pump(approving);
            Assert.Same(request, (await approving).ApprovedRequest);
            scope.Publish(f.Request.Scope with { Revision = 2 });
            await f.Pump(workflow.ContinueAsync());
            Assert.Null(workflow.Current.Review.Pin);
            Assert.Same(sealedReview, ProjectedActionReview(workflow));
            Assert.Same(request, ProjectedActionReview(workflow).ApprovedRequest);
            Assert.True(ProjectedActionReview(workflow).Sealed);
        }
        finally
        {
            action.Completion.TrySetResult(new(ActionCommandOutcome.Incomplete, action.Current));
            await f.Pump(approving);
        }
    }

    [Fact]
    public async Task GoingToTheFolderPageAfterACleanupThatRemovedPhotosRechecksTheFolderOnce()
    {
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope) { ChecksOnRetry = true };
        var (workflow, action, request) = await SettleCleanupAsync(f, scope, ActionItemState.Recycled);
        Assert.Equal(ActionStatus.Complete, ProjectedAction(workflow).Status);
        Assert.Equal(request.RequestId, ProjectedActionReview(workflow).ApprovedRequest!.RequestId);
        Assert.Empty(scope.Retries);

        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
        await f.Pump(scope.RetryEntered.Task);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));

        Assert.Single(scope.Retries);
        Assert.Equal(ScopeSessionState.Checking, workflow.Current.Scope.ScopeState);
        Assert.Equal(2, workflow.Current.Scope.Revision);
        Assert.True(workflow.Current.IsRecheckingScopeAfterCleanup);
        Assert.Null(workflow.Current.Review.Pin);
        Assert.Same(action.Current, workflow.Current.Action);

        scope.Publish(Ready(2));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));

        Assert.Single(scope.Retries);
        Assert.False(workflow.Current.IsRecheckingScopeAfterCleanup);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
    }

    [Fact]
    public async Task ARecheckStillBeingAcceptedByTheScopeIsNotSubmittedTwice()
    {
        await using var f = new ReviewFixture();
        var release = ScanTestData.Signal<bool>();
        var scope = new ScopeOwner(f.Request.Scope) { RetryRelease = release };
        try
        {
            var (workflow, _, _) = await SettleCleanupAsync(f, scope, ActionItemState.Recycled);

            await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
            await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
            Assert.Single(scope.Retries);
            Assert.False(workflow.Current.IsRecheckingScopeAfterCleanup);

            // The user picked another folder before the re-check was accepted:
            // that check is theirs, and the pending re-check label is dropped.
            scope.Publish(scope.Current with { ScopeState = ScopeSessionState.Checking, Preflight = null, Revision = 2,
                Selection = new(@"C:\Elsewhere", ScopeRecursion.TopLevelOnly) });
            await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
            Assert.Single(scope.Retries);
            Assert.False(workflow.Current.IsRecheckingScopeAfterCleanup);
            Assert.Equal(2, workflow.Current.Scope.Revision);
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task ACleanupThatRemovedNothingDoesNotRecheckTheFolder()
    {
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope) { ChecksOnRetry = true };
        var (workflow, _, _) = await SettleCleanupAsync(f, scope, ActionItemState.NotRemoved);
        Assert.Equal(ActionStatus.Complete, ProjectedAction(workflow).Status);

        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));

        Assert.Empty(scope.Retries);
        Assert.False(workflow.Current.IsRecheckingScopeAfterCleanup);
        Assert.Equal(1, workflow.Current.Scope.Revision);
    }

    [Fact]
    public async Task ACleanupThatSettlesWhileTheFolderPageIsOpenRechecksTheFolder()
    {
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope) { ChecksOnRetry = true };
        var (workflow, action, request) = await SettleCleanupAsync(f, scope, ActionItemState.Recycled, settle: false);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Empty(scope.Retries);

        action.Publish(Settled(request, ActionItemState.Recycled));
        await f.Pump(scope.RetryEntered.Task);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ChooseScope));

        Assert.Single(scope.Retries);
        Assert.True(workflow.Current.IsRecheckingScopeAfterCleanup);
    }

    // PM-013: a settled cleanup does not end the session. The user continues with
    // the same scan, without the photos that reached the Recycle Bin.
    [Fact]
    public async Task ContinuingAfterACleanupReopensTheReviewWithoutTheRemovedPhotosAndApprovesANewCleanup()
    {
        await using var f = new ReviewFixture();
        var (workflow, action, request) = await SettleCleanupAsync(f, new ScopeOwner(f.Request.Scope), ActionItemState.Recycled);
        var sealedReview = f.Owner.Current;
        f.Executor.Arrived = ScanTestData.Signal<ReviewTestRun>();

        await f.Pump(workflow.ContinueReviewAsync());

        var review = f.Owner.Current;
        Assert.NotEqual(sealedReview.Pin, review.Pin);
        Assert.Equal(sealedReview.Pin!.Scan, review.Pin!.Scan);
        Assert.False(review.Sealed);
        Assert.Equal([2L, 3L], Assert.Single(review.Groups).Members.Select(member => member.Occurrence.Token.OccurrenceId));
        Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        Assert.Same(review, workflow.Current.Review);
        Assert.Same(action.Current, ProjectedAction(workflow));
        Assert.Equal(request.RequestId, ProjectedActionReview(workflow).ApprovedRequest!.RequestId);
        await f.Eligible(2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 2));
        var command = new ApproveSelectionCommand(f.Owner.Current.Pin!, f.Owner.Current.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        await f.Pump(workflow.ApproveReviewAsync(command, TestContext.Current.CancellationToken));

        Assert.Equal(2, action.Requests.Count);
        var again = action.Requests[1];
        Assert.NotEqual(request.RequestId, again.RequestId);
        Assert.Equal(review.Pin.ReviewSessionId, again.ReviewSessionId);
        Assert.Equal([2L], again.SelectedOccurrenceIds);
        Assert.Equal(f.Request.Scan.Groups[0].Members[1].ResolvedPath, Assert.Single(again.Items).ResolvedPath);
        action.Publish(new ActionSnapshot(again.RequestId, ActionStatus.Claiming,
            [new(again.Items[0], null, ActionItemState.Waiting, ActionSafetyReason.None)], 0, 0, 0, 0, 0, ActionSafetyReason.None));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ApproveCleanup));
        Assert.Equal(again.RequestId, ProjectedAction(workflow).RequestId);
        Assert.Equal(again.RequestId, ProjectedActionReview(workflow).ApprovedRequest!.RequestId);
    }

    [Fact]
    public async Task ContinuingAfterAnUncertainOutcomeClosesThatSetUntilTheNextScan()
    {
        await using var f = new ReviewFixture();
        var (workflow, action, request) = await SettleCleanupAsync(f, new ScopeOwner(f.Request.Scope),
            ActionItemState.Recycled, settle: false);
        action.Publish(new ActionSnapshot(request.RequestId, ActionStatus.Incomplete,
            [new(request.Items[0], Guid.NewGuid(), ActionItemState.UnknownPendingRevalidation, ActionSafetyReason.None)],
            1, 1, 0, 0, 1, ActionSafetyReason.None));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ApproveCleanup));
        f.Executor.Arrived = ScanTestData.Signal<ReviewTestRun>();

        await f.Pump(workflow.ContinueReviewAsync());

        var review = f.Owner.Current;
        Assert.False(review.Sealed);
        var group = Assert.Single(review.Groups);
        Assert.True(group.Closed);
        Assert.Equal([1L, 2L, 3L], group.Members.Select(member => member.Occurrence.Token.OccurrenceId));
        Assert.Equal(ReviewStatus.Ready, review.Status);
        Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        Assert.Same(review, workflow.Current.Review);
    }

    [Theory]
    [InlineData("faulted")]
    [InlineData("running")]
    public async Task ContinuingIsIgnoredWhileTheCleanupRunsOrAfterItStopped(string state)
    {
        await using var f = new ReviewFixture();
        var (workflow, action, request) = await SettleCleanupAsync(f, new ScopeOwner(f.Request.Scope),
            ActionItemState.Recycled, settle: false);
        if (state == "faulted")
        {
            action.Publish(Settled(request, ActionItemState.NotRemoved) with
            { Status = ActionStatus.Faulted, FaultReason = ActionSafetyReason.ProgrammingFault });
            await f.Pump(workflow.NavigateAsync(WorkflowPhase.ApproveCleanup));
        }
        var before = f.Owner.Current;

        await f.Pump(workflow.ContinueReviewAsync());

        Assert.Same(before, f.Owner.Current);
        Assert.True(f.Owner.Current.Sealed);
        Assert.Equal(WorkflowPhase.ApproveCleanup, workflow.Current.CurrentPhase);
        Assert.Single(f.Executor.Runs);
    }

    private static async Task<(PhotoSiftWorkflow Workflow, DeferredAction Action, ApprovedActionRequest Request)> SettleCleanupAsync(
        ReviewFixture f, ScopeOwner scope, ActionItemState outcome, bool settle = true)
    {
        var scan = new ScanOwner();
        var action = new DeferredAction();
        var workflow = CreateActionWorkflow(scope, scan, f.Scheduler, f.Owner, action);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var command = new ApproveSelectionCommand(f.Owner.Current.Pin!, f.Owner.Current.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        var approving = workflow.ApproveReviewAsync(command, TestContext.Current.CancellationToken);
        await f.Pump(action.Entered.Task);
        var request = Assert.Single(action.Requests);
        var admitted = new ActionSnapshot(request.RequestId, ActionStatus.Claiming,
            [new(request.Items[0], null, ActionItemState.Waiting, ActionSafetyReason.None)], 0, 0, 0, 0, 0, ActionSafetyReason.None);
        action.Publish(admitted);
        action.Completion.TrySetResult(new(ActionCommandOutcome.Completed, admitted));
        await f.Pump(approving);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.ApproveCleanup));
        Assert.Equal(WorkflowPhase.ApproveCleanup, workflow.Current.CurrentPhase);
        if (settle)
        {
            action.Publish(Settled(request, outcome));
            await f.Pump(workflow.NavigateAsync(WorkflowPhase.ApproveCleanup));
        }
        return (workflow, action, request);
    }

    private static ActionSnapshot Settled(ApprovedActionRequest request, ActionItemState outcome) =>
        new(request.RequestId, ActionStatus.Complete,
            [new(request.Items[0], Guid.NewGuid(), outcome, ActionSafetyReason.None)], 1, 0,
            outcome == ActionItemState.Recycled ? 1 : 0, outcome == ActionItemState.NotRemoved ? 1 : 0, 0,
            ActionSafetyReason.None);

    [Fact]
    public async Task NavigationScanRefreshAndReopenedReviewCannotCreateAction()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var action = new DeferredAction();
        var workflow = CreateActionWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, f.Owner, action);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(f.Request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        foreach (var phase in new[] { WorkflowPhase.ApproveCleanup, WorkflowPhase.Scan, WorkflowPhase.Review })
            await f.Pump(workflow.NavigateAsync(phase));
        scan.Publish(f.Request.Scan with { Occurrences = [.. f.Request.Scan.Occurrences] });
        await f.Pump(workflow.OpenReviewAsync());
        Assert.Empty(action.Requests);
        Assert.Null(f.Owner.Current.ApprovedRequest);
        Assert.Equal(ActionStatus.Idle, ProjectedAction(workflow).Status);
    }

    [Fact]
    public async Task RepeatedWorkflowApprovalReusesActionWhileExecutingAndAfterSettlementWithoutExecutorReplay()
    {
        await using var f = new ActionFixture(1) { GateAt = "execute:1" };
        var review = new SealedRequestReview(f.Request);
        var workflow = CreateActionWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), f.Scheduler, review, f.Owner);
        var command = new ApproveSelectionCommand(new(f.Request.ReviewSessionId, f.Request.Scan),
            f.Request.SelectionRevision, f.Request.RequestId, f.Request.ApprovedUtc);
        var first = workflow.ApproveReviewAsync(command, TestContext.Current.CancellationToken);
        try
        {
            await f.PumpAsync(f.Entered.Task);
            await f.PumpAsync(workflow.ApproveReviewAsync(command, TestContext.Current.CancellationToken));
            Assert.Equal(ActionItemState.Executing, Assert.Single(ProjectedAction(workflow).Items).State);
            Assert.Single(f.Calls, call => call == "execute:1");
            f.Release.TrySetResult(true);
            await f.PumpAsync(first);
            await f.PumpAsync(workflow.ApproveReviewAsync(command, TestContext.Current.CancellationToken));
            Assert.Equal(ActionItemState.Recycled, Assert.Single(ProjectedAction(workflow).Items).State);
            Assert.Single(f.Calls, call => call == "execute:1");
            Assert.Same(f.Request, f.ClaimedRequest);
        }
        finally { f.Release.TrySetResult(true); await f.PumpAsync(first); }
    }

    [Fact]
    public async Task ActionPublicationFaultReportsItsRequestAndDoesNotFaultReview()
    {
        var scheduler = new ManualScanScheduler();
        var request = ActionFixture.RequestFor(1);
        var review = new SealedRequestReview(request);
        var action = new DeferredAction();
        var workflow = CreateActionWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), scheduler, review, action);
        var failure = new InvalidOperationException("Action projection failed");
        var healthy = new List<PhotoSiftSnapshot>();
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (ProjectedAction(snapshot).RequestId == request.RequestId) throw failure;
        };
        workflow.SnapshotChanged += (_, snapshot) => healthy.Add(snapshot);
        action.Publish(new(request.RequestId, ActionStatus.Running, [], 0, 0, 0, 0, 0, ActionSafetyReason.None));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(action.FaultReported.Task));
        Assert.Equal(request.RequestId, Assert.Single(action.ReportedFaults));
        Assert.Empty(review.ReportedFaults);
        Assert.Contains(healthy, snapshot => ProjectedAction(snapshot).RequestId == request.RequestId);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.CancelScanAsync())));
        Assert.Same(failure, error);
    }

    private static PhotoSiftWorkflow CreateActionWorkflow(IChooseScopeWorkflow scope, IScanCoordinator scan,
        IWorkflowScheduler scheduler, IReviewCoordinator review, IActionCoordinator action)
    {
        var constructor = typeof(PhotoSiftWorkflow).GetConstructor(
            [typeof(IChooseScopeWorkflow), typeof(IScanCoordinator), typeof(IWorkflowScheduler), typeof(IReviewCoordinator), typeof(IActionCoordinator)]);
        Assert.NotNull(constructor);
        return Assert.IsType<PhotoSiftWorkflow>(constructor.Invoke([scope, scan, scheduler, review, action]));
    }

    [Fact]
    public async Task ScopeRefreshAndStaleActionNotificationCannotEraseTheCurrentApprovedOperation()
    {
        var scheduler = new ManualScanScheduler();
        var scope = new ScopeOwner(Ready(1));
        var action = new DeferredAction();
        var request = ActionFixture.RequestFor(1);
        var workflow = CreateActionWorkflow(scope, new ScanOwner(), scheduler, new SealedRequestReview(request), action);
        var previous = new ActionSnapshot(Guid.NewGuid(), ActionStatus.Complete, [], 0, 0, 0, 0, 0, ActionSafetyReason.None);
        var current = new ActionSnapshot(request.RequestId, ActionStatus.Incomplete,
            [new(request.Items[0], Guid.NewGuid(), ActionItemState.UnknownPendingRevalidation, ActionSafetyReason.Interrupted)],
            1, 1, 0, 0, 1, ActionSafetyReason.Interrupted);
        action.Publish(previous);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.NavigateAsync(WorkflowPhase.ChooseScope)));
        action.Publish(current);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.NavigateAsync(WorkflowPhase.ChooseScope)));
        scope.Publish(Ready(2));
        action.Replay(previous);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.ContinueAsync()));
        Assert.Same(current, ProjectedAction(workflow));
        Assert.Equal(1, ProjectedAction(workflow).UnknownCount);
        Assert.Empty(action.Requests);
    }

    private static ActionSnapshot ProjectedAction(PhotoSiftWorkflow workflow) => ProjectedAction(workflow.Current);
    private static ReviewSnapshot ProjectedActionReview(PhotoSiftWorkflow workflow)
    {
        var property = typeof(PhotoSiftSnapshot).GetProperty("ActionReview");
        Assert.NotNull(property);
        return Assert.IsType<ReviewSnapshot>(property.GetValue(workflow.Current));
    }
    private static ActionSnapshot ProjectedAction(PhotoSiftSnapshot snapshot)
    {
        var property = typeof(PhotoSiftSnapshot).GetProperty("Action");
        Assert.NotNull(property);
        return Assert.IsType<ActionSnapshot>(property.GetValue(snapshot));
    }

    private sealed class DeferredAction : IActionCoordinator
    {
        public ActionSnapshot Current { get; private set; } = ActionSnapshot.Idle;
        public event EventHandler<ActionSnapshot>? SnapshotChanged;
        internal List<ApprovedActionRequest> Requests { get; } = [];
        internal List<Guid> ReportedFaults { get; } = [];
        internal TaskCompletionSource<bool> Entered { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<bool> FaultReported { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<ActionCommandResult> Completion { get; } = ScanTestData.Signal<ActionCommandResult>();
        public Task<ActionCommandResult> ApproveAsync(ApprovedActionRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); Entered.TrySetResult(true); return Completion.Task; }
        internal void Publish(ActionSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        internal void Replay(ActionSnapshot snapshot) => SnapshotChanged?.Invoke(this, snapshot);
        public Task ReportObserverFaultAsync(Guid requestId)
        { ReportedFaults.Add(requestId); FaultReported.TrySetResult(true); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SealedRequestReview(ApprovedActionRequest request) : IReviewCoordinator
    {
        public ReviewSnapshot Current { get; } = ReviewSnapshot.Closed with
        { Pin = new(request.ReviewSessionId, request.Scan), Sealed = true, ApprovedRequest = request };
        public event EventHandler<ReviewSnapshot>? SnapshotChanged { add { } remove { } }
        internal List<ReviewSessionPin> ReportedFaults { get; } = [];
        public Task<ReviewCommandResult> ApproveAsync(ApproveSelectionCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Approved, Current, ApprovedRequest: request));
        public Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken token) => Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Committed, Current));
        public Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken token) => throw new NotSupportedException();
        public Task<ReviewCommandResult> AffirmOverrideAsync(OverrideAffirmationCommand command, CancellationToken token) => throw new NotSupportedException();
        public Task InvalidateAsync(ScanPin? scan) => Task.CompletedTask;
        public Task ReportObserverFaultAsync(ReviewSessionPin session) { ReportedFaults.Add(session); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NeutralRebuildDuringOpenAdmissionDoesNotReplaceTheAcceptedSession()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var review = new AdmissionBarrierReview(f.Owner);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        var sessions = new HashSet<ReviewSessionPin>();
        f.Owner.SnapshotChanged += (_, snapshot) => { if (snapshot.Pin is { } pin) sessions.Add(pin); };
        scan.Publish(f.Request.Scan);
        var opening = workflow.OpenReviewAsync();
        try
        {
            await f.Pump(review.Captured.Task);
            scan.Publish(RebuildScan(f.Request.Scan));
            review.Release.TrySetResult(true);
            await f.Pump(opening);
            Assert.Single(sessions);
            Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
            Assert.Same(f.Owner.Current, workflow.Current.Review);
        }
        finally { review.Release.TrySetResult(true); await f.Pump(opening); }
    }

    [Theory]
    [InlineData("members")]
    [InlineData("token")]
    [InlineData("discoveredPath")]
    [InlineData("resolvedPath")]
    [InlineData("physicalKey")]
    [InlineData("content")]
    [InlineData("allocation")]
    [InlineData("recovery")]
    [InlineData("recoveryReason")]
    public async Task ReusedCandidateRevisionCannotHideMaterialChangeDuringOpenAdmission(string change)
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var review = new AdmissionBarrierReview(f.Owner);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        var original = f.Request.Scan.Groups[0];
        original = original with { Equality = ReviewFixture.ExactEvidence(original.Members) };
        var changed = ChangeCandidate(original, change);
        var newer = f.Request.Scan with { Groups = [changed], Occurrences = changed.Members };
        var navigatedOld = false;
        workflow.SnapshotChanged += (_, snapshot) => navigatedOld |= snapshot.CurrentPhase == WorkflowPhase.Review
            && snapshot.Review.Groups.Any(group => ReferenceEquals(group.Candidate, original));
        scan.Publish(f.Request.Scan with { Groups = [original] });
        var opening = workflow.OpenReviewAsync();
        try
        {
            await f.Pump(review.Captured.Task);
            scan.Publish(newer);
            review.Release.TrySetResult(true);
            await f.Pump(opening);
            Assert.False(navigatedOld);
            Assert.Same(changed, Assert.Single(f.Owner.Current.Groups).Candidate);
            Assert.Empty(workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
            Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        }
        finally { review.Release.TrySetResult(true); await f.Pump(opening); }
    }

    [Theory]
    [InlineData("enumerate")]
    [InlineData("equality")]
    [InlineData("equalityEndpoint")]
    [InlineData("equalityToken")]
    [InlineData("equalityContent")]
    [InlineData("equalityMultiplicity")]
    [InlineData("uncertain")]
    [InlineData("keeper")]
    public async Task InvalidSameRevisionMutationCannotNavigateOrRetainOldReviewAuthority(string change)
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var review = new AdmissionBarrierReview(f.Owner);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        var original = f.Request.Scan.Groups[0];
        original = original with { Equality = ReviewFixture.ExactEvidence(original.Members) };
        var changed = ChangeCandidate(original, change);
        var newer = f.Request.Scan with { Groups = [changed], Occurrences = changed.Members };
        var navigatedOld = false;
        workflow.SnapshotChanged += (_, snapshot) => navigatedOld |= snapshot.CurrentPhase == WorkflowPhase.Review
            && snapshot.Review.Groups.Any(group => ReferenceEquals(group.Candidate, original));
        scan.Publish(f.Request.Scan with { Groups = [original] });
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
        var opening = workflow.OpenReviewAsync();
        try
        {
            await f.Pump(review.Captured.Task);
            scan.Publish(newer);
            review.Release.TrySetResult(true);
            await f.Pump(opening);

            Assert.False(navigatedOld);
            Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
            Assert.Null(f.Owner.Current.Pin);
            Assert.Equal(ReviewStatus.Closed, f.Owner.Current.Status);
            Assert.Empty(workflow.Current.Review.Groups);
        }
        finally { review.Release.TrySetResult(true); await f.Pump(opening); }
    }

    [Theory]
    [InlineData(CandidateCategory.NearDuplicate)]
    [InlineData(CandidateCategory.Uncertain)]
    public async Task ExactOnlyWorkflowCannotAdmitNearCategoryEvidence(CandidateCategory category)
    {
        await using var f = new ReviewFixture();
        var request = ReviewCoordinatorTests.MixedRequest(f.Request, includeTerminalFailures: false);
        var group = request.Scan.Groups.Single(candidate => candidate.Category == category);
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(request.Scope), scan, f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.SetScanOptionsAsync(new(
            ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled)));
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(request.Scan with { Groups = [group] });
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));

        await f.Pump(workflow.OpenReviewAsync());

        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
        Assert.Equal(ReviewStatus.Closed, f.Owner.Current.Status);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task ConcurrentCommandsConsumeDistinctAdapterFailuresInCompletionOrder()
    {
        var scheduler = new ManualScanScheduler();
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope);
        var scan = new ScanOwner { Cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var review = new DeferredReview();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler, review);
        var cancel = workflow.CancelScanAsync();
        try
        {
            await ScanTestData.Bound(scheduler.PumpUntilAsync(scan.CancellationEntered.Task));
            var first = new InvalidOperationException("first concurrent failure");
            var second = new InvalidOperationException("second concurrent failure");
            review.InvalidationFailure = first;
            scope.Publish(f.Request.Scope with { Revision = 2 });
            var navigation = workflow.NavigateAsync(WorkflowPhase.ChooseScope);
            var navigationError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ScanTestData.Bound(scheduler.PumpUntilAsync(navigation)));
            Assert.Same(first, navigationError);
            Assert.False(cancel.IsCompleted);
            review.InvalidationFailure = second;
            scope.Publish(f.Request.Scope with { Revision = 3 });
            scan.Cancellation.TrySetResult();
            var cancellationError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ScanTestData.Bound(scheduler.PumpUntilAsync(cancel)));
            Assert.Same(second, cancellationError);
            await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.ContinueAsync()));
            Assert.Equal(WorkflowPhase.ConfirmRules, workflow.Current.CurrentPhase);
        }
        finally
        {
            scan.Cancellation.TrySetResult();
            try { await ScanTestData.Bound(scheduler.PumpUntilAsync(cancel)); }
            catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task NeutralRebuiltCandidateArraysPreserveLiveReviewAndLaterPublications()
    {
        await using var f = new ReviewFixture();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(f.Request.Scope), scan, f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        var group = f.Request.Scan.Groups[0];
        group = group with { Equality = [new(group.Members[0].Token, group.Members[1].Token,
            group.Members[0].Content!, group.Members[1].Content!), new(group.Members[1].Token, group.Members[2].Token,
            group.Members[1].Content!, group.Members[2].Content!)] };
        var original = f.Request.Scan with { Status = ScanStatus.Incomplete, Groups = [group] };
        scan.Publish(original);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Run();
        var session = f.Owner.Current.Pin;
        var rebuilt = RebuildScan(original);
        scan.Publish(rebuilt);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
        Assert.Equal(session, f.Owner.Current.Pin);
        await f.Eligible(1, 2, 3);
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Review));
        Assert.Same(f.Owner.Current, workflow.Current.Review);
        Assert.Equal(0, workflow.Current.Review.Unresolved.PendingReviewMembers);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
        Assert.Equal([1L], workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
        await f.Pump(f.Owner.ReportObserverFaultAsync(session!));
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Review));
        Assert.Equal(ReviewStatus.Faulted, workflow.Current.Review.Status);
        Assert.Equal(session, workflow.Current.Review.Pin);
        Assert.Same(rebuilt, workflow.Current.Scan);
    }

    [Fact]
    public async Task MultipleAdapterFailuresAreDeliveredOnceWithoutBlockingCancellationOrLaterCommands()
    {
        var scheduler = new ManualScanScheduler();
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope);
        var scan = new ScanOwner();
        var first = new InvalidOperationException("first adapter failure");
        var second = new InvalidOperationException("second adapter failure");
        var review = new DeferredReview { InvalidationFailure = first };
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler, review);
        scope.Publish(f.Request.Scope with { Revision = 2 });
        review.InvalidationFailure = second;
        scope.Publish(f.Request.Scope with { Revision = 3 });
        var cancel = workflow.CancelScanAsync();
        var observedFirst = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScanTestData.Bound(scheduler.PumpUntilAsync(cancel)));
        Assert.Same(first, observedFirst);
        Assert.Equal(1, scan.Cancellations);
        var next = workflow.NavigateAsync(WorkflowPhase.ChooseScope);
        var observedSecond = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScanTestData.Bound(scheduler.PumpUntilAsync(next)));
        Assert.Same(second, observedSecond);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.ContinueAsync()));
        Assert.Equal(WorkflowPhase.ConfirmRules, workflow.Current.CurrentPhase);
    }

    [Fact]
    public async Task CommandFailureHasPriorityAndLeavesAdapterDiagnosticForLaterDelivery()
    {
        var scheduler = new ManualScanScheduler();
        await using var f = new ReviewFixture();
        var scope = new ScopeOwner(f.Request.Scope);
        var adapterFailure = new InvalidOperationException("adapter diagnostic");
        var commandFailure = new InvalidOperationException("command failure");
        var scan = new ScanOwner { Cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        scan.Cancellation.SetException(commandFailure);
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler,
            new DeferredReview { InvalidationFailure = adapterFailure });
        scope.Publish(f.Request.Scope with { Revision = 2 });
        var commandError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.CancelScanAsync())));
        Assert.Same(commandFailure, commandError);
        Assert.Equal(1, scan.Cancellations);
        var diagnostic = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.NavigateAsync(WorkflowPhase.ChooseScope))));
        Assert.Same(adapterFailure, diagnostic);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(workflow.ContinueAsync()));
        Assert.Equal(WorkflowPhase.ConfirmRules, workflow.Current.CurrentPhase);
    }

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
        var diagnostic = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PumpCaptured(f.Scheduler, workflow.NavigateAsync(WorkflowPhase.Scan), capture));
        Assert.Equal("old presentation failure", diagnostic.Message);
        await PumpCaptured(f.Scheduler, workflow.NavigateAsync(WorkflowPhase.Review), capture);
        await PumpCaptured(f.Scheduler, workflow.OpenReviewAsync(), capture);
        Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        Assert.Equal(ReviewStatus.Revalidating, f.Owner.Current.Status);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameScanNewerCandidateReplacesReviewDirectlyWithEmptySelection(bool reusedRevision)
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
            if (snapshot.Groups.Any(g => g.Candidate.PotentialRecovery.Bytes == 88888))
                replacement.TrySetResult(true);
        };
        scan.Publish(f.Request.Scan with { Groups = [f.Request.Scan.Groups[0] with
            {
                GroupRevision = reusedRevision ? 1 : 2,
                PotentialRecovery = new(88888, RecoveryUnavailableReason.None),
            }] });
        try { await f.Pump(replacement.Task); }
        catch (TimeoutException) { }
        Assert.True(replacement.Task.IsCompletedSuccessfully, "A newer same-Scan candidate must replace the active Review.");
        Assert.NotEqual(previous.Pin, f.Owner.Current.Pin);
        Assert.Empty(f.Owner.Current.Groups[0].SelectedOccurrenceIds);
        Assert.False(observedClosed);
        Assert.Equal(ReviewCommandOutcome.Stale, (await f.Change(stale)).Outcome);
    }

    [Fact]
    public async Task SameSimilarityValuesDoNotReplaceReviewButChangedEvidenceDoes()
    {
        await using var f = new ReviewFixture();
        var request = ReviewCoordinatorTests.MixedRequest(f.Request,
            includeTerminalFailures: false);
        var scan = new ScanOwner();
        var review = new ApprovalProbeReview(f.Owner);
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(request.Scope), scan,
            f.Scheduler, review);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        Assert.Equal(1, review.Opens);

        var sameGroups = request.Scan.Groups.Select(group => group with
        {
            Similarity = group.Similarity.Reverse().Select(edge => edge with
            {
                Left = edge.Right,
                Right = edge.Left,
            }).ToImmutableArray(),
        }).ToImmutableArray();
        scan.Publish(request.Scan with { Groups = sameGroups });
        await f.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
        Assert.Equal(1, review.Opens);

        var near = sameGroups[1];
        var changedGroups = sameGroups.SetItem(1, near with
        {
            Similarity = near.Similarity.SetItem(0,
                near.Similarity[0] with { HammingDistance = 2 }),
        });
        scan.Publish(request.Scan with { Groups = changedGroups });
        for (var turn = 0; turn < 100 && review.Opens < 2; turn++)
        {
            f.Scheduler.TryRunOne();
            await Task.Yield();
        }

        Assert.Equal(2, review.Opens);
    }

    [Fact]
    public async Task MixedReviewFlowsThroughRealWorkflowSelectionAccountingAndApproval()
    {
        await using var f = new ReviewFixture();
        var request = ReviewCoordinatorTests.MixedRequest(f.Request,
            includeTerminalFailures: true);
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(request.Scope), scan,
            f.Scheduler, f.Owner);
        await f.Pump(workflow.ContinueAsync());
        await f.Pump(workflow.ConfirmExactRulesAsync());
        scan.Publish(request.Scan);
        await f.Pump(workflow.OpenReviewAsync());
        await f.Eligible(request);
        var snapshot = f.Owner.Current;
        var exact = snapshot.Groups[0].Candidate;
        var selecting = workflow.ChangeReviewSelectionAsync(new(
            new(snapshot.Pin!, exact.GroupId, exact.GroupRevision,
                snapshot.SelectionRevision), SelectionCommandKind.Toggle, [1],
            Guid.NewGuid()), TestContext.Current.CancellationToken);
        await f.Pump(selecting);
        var selected = await selecting;
        snapshot = f.Owner.Current;

        var approving = workflow.ApproveReviewAsync(new(
            snapshot.Pin!, snapshot.SelectionRevision, Guid.NewGuid(),
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await f.Pump(approving);
        var approved = await approving;

        Assert.Equal(ReviewCommandOutcome.Committed, selected.Outcome);
        Assert.Equal(ReviewCommandOutcome.Approved, approved.Outcome);
        Assert.Equal([1L], approved.ApprovedRequest!.SelectedOccurrenceIds);
        Assert.Single(approved.ApprovedRequest.Items);
        Assert.Equal(2, approved.ApprovedRequest.Disclosure.Unresolved.FailedScanRows);
        var terminalFailures = request.Scan.Occurrences
            .Where(row => row.VisualFeatures.State == ScanStageState.FailedRetryable
                || row.NearDuplicate.State == ScanStageState.FailedRetryable)
            .ToArray();
        Assert.Collection(terminalFailures, _ => { }, _ => { });
        Assert.All(terminalFailures, row =>
        {
            Assert.Equal(ScanStageState.Succeeded, row.Enumerate.State);
            Assert.Equal(ScanStageState.Succeeded, row.Exact.State);
            Assert.NotNull(row.Content);
        });
        Assert.Equal(3, approved.ApprovedRequest.Disclosure.Unresolved.UnselectedSurvivorMembers);
        Assert.Equal(4096, approved.ApprovedRequest.Disclosure.Recovery.Bytes);
        Assert.True(workflow.Current.Review.Sealed);
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

    [Theory]
    [InlineData(WorkflowNonApprovalEvent.RuleConfirmation)]
    [InlineData(WorkflowNonApprovalEvent.ScanStart)]
    [InlineData(WorkflowNonApprovalEvent.ScanRetry)]
    [InlineData(WorkflowNonApprovalEvent.ScanCancel)]
    [InlineData(WorkflowNonApprovalEvent.Navigate)]
    [InlineData(WorkflowNonApprovalEvent.OpenReview)]
    public async Task CurrentWorkflowEventsNeverInvokeApprovalAuthority(
        WorkflowNonApprovalEvent action)
    {
        await using var fixture = new ReviewFixture();
        var scope = new ScopeOwner(fixture.Request.Scope);
        var scan = new ScanOwner();
        var review = new ApprovalProbeReview(fixture.Owner);
        var workflow = new PhotoSiftWorkflow(scope, scan, fixture.Scheduler, review);
        await fixture.Pump(workflow.ContinueAsync());
        if (action != WorkflowNonApprovalEvent.RuleConfirmation)
        {
            await fixture.Pump(workflow.ConfirmExactRulesAsync());
        }

        switch (action)
        {
            case WorkflowNonApprovalEvent.RuleConfirmation:
                await fixture.Pump(workflow.ConfirmExactRulesAsync());
                Assert.NotNull(workflow.Current.Rules);
                break;
            case WorkflowNonApprovalEvent.ScanStart:
                await fixture.Pump(workflow.StartScanAsync());
                Assert.Single(scan.Starts);
                break;
            case WorkflowNonApprovalEvent.ScanRetry:
                await fixture.Pump(workflow.StartScanAsync());
                scan.Publish(fixture.Request.Scan with { Status = ScanStatus.Incomplete });
                await fixture.Pump(workflow.StartScanAsync());
                Assert.Equal(2, scan.Starts.Count);
                break;
            case WorkflowNonApprovalEvent.ScanCancel:
                await fixture.Pump(workflow.StartScanAsync());
                await fixture.Pump(workflow.CancelScanAsync());
                Assert.Equal(1, scan.Cancellations);
                break;
            case WorkflowNonApprovalEvent.Navigate:
                await fixture.Pump(workflow.NavigateAsync(WorkflowPhase.Scan));
                Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
                break;
            case WorkflowNonApprovalEvent.OpenReview:
                scan.Publish(fixture.Request.Scan);
                await fixture.Pump(workflow.OpenReviewAsync());
                Assert.Equal(1, review.Opens);
                Assert.NotNull(workflow.Current.Review.Pin);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }

        Assert.Equal(0, review.Approvals);
        Assert.Null(review.Current.ApprovedRequest);
        Assert.False(review.Current.Sealed);
        Assert.Null(workflow.Current.Review.ApprovedRequest);
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
    public async Task InvalidOptionsRemainVisibleAndCannotConfirmOrStart()
    {
        var scheduler = new ManualScheduler();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        var invalid = new ScanOptions(ExactDuplicateMode.Disabled, NearDuplicateMode.Conservative);

        await scheduler.Finish(workflow.SetScanOptionsAsync(invalid));
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        await scheduler.Finish(workflow.StartScanAsync());

        Assert.Equal(invalid, workflow.Current.Options);
        Assert.False(workflow.Current.Options.IsValid);
        Assert.Null(workflow.Current.Rules);
        Assert.Empty(scan.Starts);
        Assert.False(ShellPhasePolicy.CanStart(workflow.Current.Scope, workflow.Current.Rules));
    }

    [Fact]
    public async Task OptionsCanChangeOnTheFolderStepWithoutLeavingIt()
    {
        var scheduler = new ManualScheduler();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        var exactOnly = new ScanOptions(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled);

        await scheduler.Finish(workflow.SetScanOptionsAsync(exactOnly));

        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Equal(exactOnly, workflow.Current.Options);
        Assert.Null(workflow.Current.Rules);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        await scheduler.Finish(workflow.StartScanAsync());
        Assert.Equal(exactOnly, Assert.Single(scan.Starts).Options);
    }

    [Fact]
    public async Task OptionsCannotChangeBeforeTheFolderIsReady()
    {
        var scheduler = new ManualScheduler();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(new(InitialWorkflow.Create(), "Choose")), new ScanOwner(), scheduler);
        var before = workflow.Current.Options;

        await scheduler.Finish(workflow.SetScanOptionsAsync(
            new(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled)));

        Assert.Equal(before, workflow.Current.Options);
    }

    [Fact]
    public async Task OptionChangeRevokesPriorGroupsAndPinsFreshRevisionToNextScan()
    {
        var scheduler = new ManualScheduler();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var oldRules = workflow.Current.Rules!;
        scan.Publish(EmptyScan(new(Guid.NewGuid(), 1, oldRules.RuleSetRevision, 1), ScanStatus.Complete));
        scheduler.Drain();
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.ConfirmRules));

        var exactOnly = new ScanOptions(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled);
        await scheduler.Finish(workflow.SetScanOptionsAsync(exactOnly));

        Assert.Null(workflow.Current.Rules);
        Assert.Null(workflow.Current.Scan.Pin);
        Assert.Equal([(1L, (long?)null)], scan.Invalidations);
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        await scheduler.Finish(workflow.StartScanAsync());
        var started = Assert.Single(scan.Starts);
        Assert.Equal(exactOnly, started.Options);
        Assert.True(started.RuleSetRevision > oldRules.RuleSetRevision);
    }

    [Fact]
    public async Task OptionChangeOnTheFolderStepAfterAScanRevokesItsResults()
    {
        var scheduler = new ManualScheduler();
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var oldRules = workflow.Current.Rules!;
        scan.Publish(EmptyScan(new(Guid.NewGuid(), 1, oldRules.RuleSetRevision, 1), ScanStatus.Complete));
        scheduler.Drain();
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.ChooseScope));

        await scheduler.Finish(workflow.SetScanOptionsAsync(
            new(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled)));

        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Null(workflow.Current.Rules);
        Assert.Null(workflow.Current.Scan.Pin);
        Assert.Equal(ReviewSnapshot.Closed, workflow.Current.Review);
        Assert.Equal([(1L, (long?)null)], scan.Invalidations);
    }

    [Fact]
    public async Task ScanAdmissionRejectsOptionMutationAfterLeavingConfirmRules()
    {
        var scheduler = new ManualScheduler();
        var releaseStart = ScanTestData.Signal<bool>();
        var scan = new ScanOwner { StartRelease = releaseStart };
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var confirmedRules = workflow.Current.Rules!;
        var confirmedOptions = workflow.Current.Options;

        var start = workflow.StartScanAsync();
        scheduler.Drain();
        var admittedRules = await scan.StartEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
        Assert.Equal(confirmedRules, admittedRules);

        await scheduler.Finish(workflow.SetScanOptionsAsync(new(
            ExactDuplicateMode.Enabled,
            NearDuplicateMode.Disabled)));

        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
        Assert.Equal(confirmedOptions, workflow.Current.Options);
        Assert.Equal(confirmedRules, workflow.Current.Rules);
        Assert.Equal(confirmedRules, Assert.Single(scan.Starts));
        releaseStart.SetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

    [Fact]
    public void ConstructionStartsHistoricalRecordLoadWithoutUsingLiveActionAuthority()
    {
        var recovery = new RecoveryOwner();
        _ = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), new ManualScheduler(),
            null, null, recovery);

        Assert.Equal(1, recovery.Loads);
        Assert.Empty(recovery.Revalidations);
    }

    [Fact]
    public void HistoricalRecoveryPublicationIsProjectedIndependently()
    {
        var scheduler = new ManualScheduler();
        var recovery = new RecoveryOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), scheduler,
            null, null, recovery);
        var snapshot = new ActionRecoverySnapshot(ActionRecoveryStatus.Ready, [], null, false);

        recovery.Publish(snapshot);
        scheduler.Drain();

        Assert.Same(snapshot, workflow.Current.ActionRecovery);
        Assert.Equal(ActionStatus.Idle, workflow.Current.Action.Status);
        Assert.Equal(ReviewSnapshot.Closed, workflow.Current.Review);
    }

    [Fact]
    public async Task ExplicitHistoricalRevalidationForwardsExactRequestAndCancellation()
    {
        var recovery = new RecoveryOwner();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), new ManualScheduler(),
            null, null, recovery);
        var requestId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        await workflow.RevalidateUnknownAsync(requestId, cancellation.Token);

        var command = Assert.Single(recovery.Revalidations);
        Assert.Equal(requestId, command.RequestId);
        Assert.Equal(cancellation.Token, command.Token);
    }

    private static ShellSnapshot Ready(long revision) => new(InitialWorkflow.Create(), "Ready", ScopeSessionState.Ready, Revision: revision);
    private static ScanSnapshot RebuildScan(ScanSnapshot original) => original with
    {
        Occurrences = original.Occurrences.Reverse().Select(member => ReviewFixture.Copy(member)).ToImmutableArray(),
        Groups = original.Groups.Reverse().Select(group => group with
        {
            Members = group.Members.Reverse().Select(member => ReviewFixture.Copy(member)).ToImmutableArray(),
            Equality = group.Equality.Reverse().Select(edge => new EqualityEvidence(
                edge.Right, edge.Left, edge.RightContent, edge.LeftContent)).ToImmutableArray(),
        }).ToImmutableArray(),
    };
    private static CandidateGroup ChangeCandidate(CandidateGroup group, string change)
    {
        var member = group.Members[0];
        if (change == "members")
        {
            var members = group.Members.Take(2).ToImmutableArray();
            return group with { Members = members, Equality = ReviewFixture.ExactEvidence(members) };
        }
        if (change == "token")
        {
            var changed = ReviewFixture.Copy(member,
                token: member.Token with { Attempt = 2 });
            var members = group.Members.SetItem(0, changed);
            return group with { Members = members, Equality = ReviewFixture.ExactEvidence(members) };
        }
        if (change == "physicalKey")
        {
            var replacementKey = group.Members[1].PhysicalFileKey;
            var replacementContent = new ContentRevision(
                replacementKey!.Value,
                member.Content!.ByteLength,
                member.Content.Sha256);
            var changed = ReviewFixture.Copy(member, key: replacementKey,
                content: replacementContent);
            var members = group.Members.SetItem(0, changed);
            return group with { Members = members, Equality = ReviewFixture.ExactEvidence(members) };
        }
        if (change == "content")
        {
            var members = group.Members.Select(value => ReviewFixture.Copy(value,
                content: new ContentRevision(
                    value.Content!.PhysicalFileKey,
                    value.Content.ByteLength,
                    new string('F', 64)))).ToImmutableArray();
            return group with { Members = members, Equality = ReviewFixture.ExactEvidence(members) };
        }
        var key = change == "physicalKey" ? group.Members[1].PhysicalFileKey : member.PhysicalFileKey;
        var altered = new ScanFileOccurrence(change == "token" ? member.Token with { Attempt = 2 } : member.Token,
            change == "discoveredPath" ? member.DiscoveredPath + ".changed" : member.DiscoveredPath,
            change == "resolvedPath" ? member.ResolvedPath + ".changed" : member.ResolvedPath,
            key, change == "enumerate" ? new(ScanStageState.Running, ScanReason.None) : member.Enumerate,
            member.Exact, new(key!.Value, member.Content!.ByteLength,
                change == "content" ? new string('F', 64) : member.Content.Sha256),
            change == "allocation" ? new(8192, 1, true) : member.Allocation);
        return change switch
        {
            "equality" => group with { Equality = [] },
            "equalityEndpoint" => group with { Equality = [group.Equality[0] with
                { Left = member.Token with { OccurrenceId = 999 } }] },
            "equalityToken" => group with { Equality = [group.Equality[0] with { Left = member.Token with { Attempt = 2 } }] },
            "equalityContent" => group with { Equality = [group.Equality[0] with
                { LeftContent = new(member.PhysicalFileKey!.Value, 2, member.Content!.Sha256) }] },
            "equalityMultiplicity" => group with { Equality = [group.Equality[0], group.Equality[0]] },
            "uncertain" => group with { Uncertain = true },
            "keeper" => group with { KeeperOccurrenceId = 2 },
            "recovery" => group with { PotentialRecovery = new(1, RecoveryUnavailableReason.None) },
            "recoveryReason" => group with { PotentialRecovery = new(null, RecoveryUnavailableReason.UnknownAllocation) },
            _ => group with { Members = group.Members.SetItem(0, altered) },
        };
    }
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
    private sealed class PostedScheduler : IWorkflowScheduler { public void Post(System.Action action) => ThreadPool.QueueUserWorkItem(_ => action()); }
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
        public Task<ReviewCommandResult> AffirmOverrideAsync(OverrideAffirmationCommand command, CancellationToken cancellationToken) => owner.AffirmOverrideAsync(command, cancellationToken);
        public Task<ReviewCommandResult> ApproveAsync(ApproveSelectionCommand command, CancellationToken cancellationToken) => owner.ApproveAsync(command, cancellationToken);
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
        internal Exception? InvalidationFailure { get; set; }
        internal TaskCompletionSource<bool> Invalidated { get; } = ScanTestData.Signal<bool>();
        public Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken)
        { Request = request; Entered.TrySetResult(true); return Done.Task; }
        public Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReviewCommandResult> AffirmOverrideAsync(OverrideAffirmationCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReviewCommandResult> ApproveAsync(ApproveSelectionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
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
    private sealed class ApprovalProbeReview(IReviewCoordinator owner) : IReviewCoordinator
    {
        internal int Opens { get; private set; }
        internal int Approvals { get; private set; }
        public ReviewSnapshot Current => owner.Current;
        public event EventHandler<ReviewSnapshot>? SnapshotChanged
        {
            add => owner.SnapshotChanged += value;
            remove => owner.SnapshotChanged -= value;
        }
        public Task<ReviewCommandResult> OpenAsync(
            ReviewOpenRequest request,
            CancellationToken cancellationToken)
        {
            Opens++;
            return owner.OpenAsync(request, cancellationToken);
        }
        public Task<ReviewCommandResult> ChangeSelectionAsync(
            SelectionCommand command,
            CancellationToken cancellationToken) =>
            owner.ChangeSelectionAsync(command, cancellationToken);
        public Task<ReviewCommandResult> AffirmOverrideAsync(
            OverrideAffirmationCommand command,
            CancellationToken cancellationToken) =>
            owner.AffirmOverrideAsync(command, cancellationToken);
        public Task<ReviewCommandResult> ApproveAsync(
            ApproveSelectionCommand command,
            CancellationToken cancellationToken)
        {
            Approvals++;
            return owner.ApproveAsync(command, cancellationToken);
        }
        public Task InvalidateAsync(ScanPin? currentScan) => owner.InvalidateAsync(currentScan);
        public Task ReportObserverFaultAsync(ReviewSessionPin session) =>
            owner.ReportObserverFaultAsync(session);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ManualScheduler : IWorkflowScheduler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<System.Action> queue = new();
        public void Post(System.Action action) => queue.Enqueue(action);
        public void Drain() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Finish(Task task) { Drain(); await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); Drain(); }
    }
    private sealed class ScopeOwner(ShellSnapshot current) : IChooseScopeWorkflow
    {
        public ShellSnapshot Current { get; private set; } = current;
        public event EventHandler<ShellSnapshot>? SnapshotChanged;
        public List<RetryScopePreflightCommand> Retries { get; } = [];
        public TaskCompletionSource<bool> RetryEntered { get; } = ScanTestData.Signal<bool>();
        // Like the real owner: a retry publishes Checking under the next revision.
        public bool ChecksOnRetry { get; init; }
        // Like the real owner: the retry call does not return until the check ends.
        public TaskCompletionSource<bool>? RetryRelease { get; init; }
        public void Publish(ShellSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        public Task HandleAsync(ChooseScopeCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(ChangeScopeRecursionCommand command, CancellationToken token) => Task.CompletedTask;
        public async Task HandleAsync(RetryScopePreflightCommand command, CancellationToken token)
        {
            Retries.Add(command);
            if (ChecksOnRetry)
                Publish(Current with { ScopeState = ScopeSessionState.Checking, Preflight = null, Revision = Current.Revision + 1 });
            RetryEntered.TrySetResult(true);
            if (RetryRelease is not null) await RetryRelease.Task;
        }
    }
    private sealed class ScanOwner : IScanCoordinator
    {
        public ScanSnapshot Current { get; private set; } = EmptyScan();
        public List<ExactRuleConfirmation> Starts { get; } = [];
        public List<(long ScopeRevision, long? RuleRevision)> Invalidations { get; } = [];
        public TaskCompletionSource<ExactRuleConfirmation> StartEntered { get; } =
            ScanTestData.Signal<ExactRuleConfirmation>();
        public TaskCompletionSource<bool>? StartRelease { get; init; }
        public TaskCompletionSource? Cancellation { get; init; }
        internal int Cancellations { get; private set; }
        internal TaskCompletionSource<bool> CancellationEntered { get; } = ScanTestData.Signal<bool>();
        public event EventHandler<ScanSnapshot>? SnapshotChanged;
        public void Publish(ScanSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        internal void Replay(ScanSnapshot snapshot) => SnapshotChanged?.Invoke(this, snapshot);
        public async Task StartAsync(ShellSnapshot scope, ExactRuleConfirmation rules, CancellationToken token)
        {
            Starts.Add(rules);
            StartEntered.TrySetResult(rules);
            if (StartRelease is not null) await StartRelease.Task;
        }
        public Task InvalidateAsync(long scopeRevision, long? ruleRevision)
        {
            Invalidations.Add((scopeRevision, ruleRevision));
            return Task.CompletedTask;
        }
        public Task CancelAsync() { Cancellations++; CancellationEntered.TrySetResult(true); return Cancellation?.Task ?? Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecoveryOwner : IActionRecoveryCoordinator
    {
        public ActionRecoverySnapshot Current { get; private set; } = ActionRecoverySnapshot.NotLoaded;
        public event EventHandler<ActionRecoverySnapshot>? SnapshotChanged;
        internal int Loads { get; private set; }
        internal List<(Guid RequestId, CancellationToken Token)> Revalidations { get; } = [];
        internal void Publish(ActionRecoverySnapshot snapshot)
        {
            Current = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
        public Task LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Task.CompletedTask;
        }
        public Task RevalidateUnknownAsync(Guid requestId, CancellationToken cancellationToken)
        {
            Revalidations.Add((requestId, cancellationToken));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public enum WorkflowNonApprovalEvent
{
    RuleConfirmation,
    ScanStart,
    ScanRetry,
    ScanCancel,
    Navigate,
    OpenReview,
}



