using PhotoSift.Application.Review;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ReviewReplacementTests
{
    [Fact]
    public async Task ObserverFaultReportIsQueuedAndOnlyFaultsItsExactLiveSessionBeforeCancellation()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var old = f.Owner.Current.Pin!;
        await f.Run();
        f.Executor.Arrived = ScanTestData.Signal<ReviewTestRun>();
        await f.Open();
        var run = await f.Run();
        var canceled = ScanTestData.Signal<ReviewStatus>();
        run.OnCancel = () => { canceled.TrySetResult(f.Owner.Current.Status); return Task.CompletedTask; };
        var current = f.Owner.Current;
        await f.Pump(f.Owner.ReportObserverFaultAsync(old));
        Assert.Same(current, f.Owner.Current);
        var reported = f.Owner.ReportObserverFaultAsync(current.Pin!);
        Assert.False(reported.IsCompleted);
        Assert.Same(current, f.Owner.Current);
        await f.Pump(reported);
        Assert.Equal(ReviewStatus.Faulted, f.Owner.Current.Status);
        Assert.Equal(ReviewUnavailableReason.ProgrammingFault, f.Owner.Current.FaultReason);
        await f.Pump(canceled.Task);
        Assert.Equal(ReviewStatus.Faulted, await canceled.Task);
        await f.Pump(f.Owner.InvalidateAsync(null));
        await f.Pump(f.Owner.ReportObserverFaultAsync(current.Pin!));
        Assert.Null(f.Owner.Current.Pin);
        await f.Pump(f.Owner.DisposeAsync().AsTask());
        await f.Pump(f.Owner.ReportObserverFaultAsync(current.Pin!));
        Assert.Null(f.Owner.Current.Pin);
    }

    [Fact]
    public async Task DelayedLowerRevisionInvalidationCannotReopenAnObsoleteScan()
    {
        await using var f = new ReviewFixture();
        var newer = f.Request.Scan.Pin! with { ScopeRevision = 7, RuleSetRevision = 9 };
        await f.Pump(f.Owner.InvalidateAsync(newer));
        await f.Pump(f.Owner.InvalidateAsync(f.Request.Scan.Pin));
        var result = await f.Open();
        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Null(result.Snapshot.Pin);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task WorkerProgrammingFaultIsPublishedBeforeItsCancellationCallback()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var run = await f.Run();
        var canceled = ScanTestData.Signal<ReviewStatus>();
        run.OnCancel = () => { canceled.TrySetResult(f.Owner.Current.Status); return Task.CompletedTask; };
        run.Done.TrySetException(new InvalidOperationException("worker fault"));
        await f.Pump(canceled.Task);
        Assert.Equal(ReviewStatus.Faulted, await canceled.Task);
        await f.Pump(run.Disposed.Task);
    }

    [Fact]
    public async Task FullResultLaneStillAdmitsSelectionAndReplacementBeforeWaitingEvidence()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Run();
        var first = f.Owner.CommitRevalidationAsync(f.Evidence(1)).AsTask();
        var waiting = f.Owner.CommitRevalidationAsync(f.Evidence(2)).AsTask();
        var command = f.Owner.ChangeSelectionAsync(f.Command(SelectionCommandKind.Toggle, 1), TestContext.Current.CancellationToken);
        var replacement = f.Owner.OpenAsync(f.Request, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        await f.Pump(command);
        Assert.True(await first);
        Assert.Equal(ReviewCommandOutcome.OverrideRequired, (await command).Outcome);
        await f.Pump(replacement);
        await f.Pump(waiting);
        Assert.False(await waiting);
        Assert.Empty(f.Owner.Current.Groups[0].SelectedOccurrenceIds);
    }

    [Fact]
    public async Task InvalidationRejectsDelayedOldOpenAndPreservesANewerSession()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var original = f.Owner.Current.Pin!;
        var next = f.Request.Scan.Pin! with { AnalysisRevision = 2 };
        await f.Pump(f.Owner.InvalidateAsync(next));
        Assert.Equal(ReviewCommandOutcome.Stale, (await f.Open()).Outcome);
        Assert.Null(f.Owner.Current.Pin);
        await f.Pump(f.Owner.InvalidateAsync(null));
        Assert.Equal(ReviewCommandOutcome.Stale, (await f.Open()).Outcome);
        Assert.NotNull(original);
    }

    [Fact]
    public async Task CompletedRunReconcilesPendingMembersAndDisposalFaultStaysVisible()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var run = await f.Run();
        run.DisposalFailure = new InvalidOperationException("dispose fault");
        run.Done.TrySetResult();
        var finished = ScanTestData.Signal<bool>();
        f.Owner.SnapshotChanged += (_, snapshot) =>
        { if (snapshot.Status == ReviewStatus.Faulted) finished.TrySetResult(true); };
        await f.Pump(finished.Task);
        Assert.Equal(0, f.Owner.Current.Unresolved.PendingReviewMembers);
        Assert.Equal(3, f.Owner.Current.Unresolved.UnavailableReviewMembers);
    }

    [Fact]
    public async Task ExplicitReviewAndNewGroupRevisionStartEmptyAndRejectPredecessorCommands()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Eligible(1, 2, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var stale = f.Command(SelectionCommandKind.Toggle, 2);
        var old = f.Owner.Current;
        var request = f.Request with { Scan = f.Request.Scan with { Groups = [f.Request.Scan.Groups[0] with { GroupRevision = 2 }] } };
        await f.Open(request);
        var result = await f.Change(stale);
        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Empty(Assert.Single(result.Snapshot.Groups).SelectedOccurrenceIds);
        Assert.Equal(2, result.Snapshot.Groups[0].Candidate.GroupRevision);
        Assert.NotEqual(old.Pin, result.Snapshot.Pin);
        var replacement = await f.Open(request);
        Assert.NotEqual(result.Snapshot.Pin, replacement.Snapshot.Pin);
        Assert.Empty(replacement.Snapshot.Groups[0].SelectedOccurrenceIds);
    }

    [Fact]
    public async Task ReplacementCommitsBeforeCancellationAndWaitsForCallbacksBeforeDisposal()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var old = await f.Run();
        var staleEvidence = f.Evidence(1);
        var entered = ScanTestData.Signal<ReviewSessionPin>();
        var release = ScanTestData.Signal<bool>();
        var rejected = ScanTestData.Signal<bool>();
        old.OnCancel = async () =>
        {
            entered.TrySetResult(f.Owner.Current.Pin!);
            rejected.TrySetResult(await old.Sink.CommitRevalidationAsync(staleEvidence));
            await release.Task;
        };
        try
        {
            await f.Open();
            await f.Pump(entered.Task);
            Assert.NotEqual(old.Pin, await entered.Task);
            await f.Pump(rejected.Task);
            Assert.False(await rejected.Task);
            Assert.False(old.Disposed.Task.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        await f.Pump(old.Disposed.Task);
    }

    [Fact]
    public async Task ObserverReentryIsQueuedAndPublicationsRetainCommitOrder()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Eligible(1, 2, 3);
        var revisions = new List<long>();
        Task<ReviewCommandResult>? nested = null;
        f.Owner.SnapshotChanged += (_, snapshot) =>
        {
            revisions.Add(snapshot.SelectionRevision);
            if (snapshot.SelectionRevision != 2) return;
            nested = f.Owner.ChangeSelectionAsync(f.Command(SelectionCommandKind.Toggle, 2), TestContext.Current.CancellationToken);
            Assert.False(f.Scheduler.TryRunOne());
            Assert.False(nested.IsCompleted);
        };
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        await f.Pump(nested!);
        Assert.Equal([2L, 3L], revisions);
        Assert.Equal([1L, 2L], f.Owner.Current.Groups[0].SelectedOccurrenceIds.Order());
    }

    [Fact]
    public async Task ObserverFaultIsVisibleBeforeCancellationAndLateAcknowledgmentSettles()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var run = await f.Run();
        var canceled = ScanTestData.Signal<ReviewStatus>();
        var stale = ScanTestData.Signal<bool>();
        run.OnCancel = async () =>
        {
            canceled.TrySetResult(f.Owner.Current.Status);
            stale.TrySetResult(await run.Sink.CommitRevalidationAsync(f.Evidence(2)));
        };
        f.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Unresolved.PendingReviewMembers == 2 && snapshot.Status != ReviewStatus.Faulted)
                throw new InvalidOperationException("observer");
        };
        var ack = f.Owner.CommitRevalidationAsync(f.Evidence(1)).AsTask();
        await f.Pump(ack);
        Assert.True(await ack);
        await f.Pump(canceled.Task);
        Assert.Equal(ReviewStatus.Faulted, await canceled.Task);
        Assert.Equal(ReviewUnavailableReason.ProgrammingFault, f.Owner.Current.FaultReason);
        await f.Pump(stale.Task);
        Assert.False(await stale.Task);
        await f.Pump(run.Disposed.Task);
        Assert.Equal(ReviewCommandOutcome.Faulted, (await f.Change(f.Command(SelectionCommandKind.Toggle, 1))).Outcome);
    }
}
