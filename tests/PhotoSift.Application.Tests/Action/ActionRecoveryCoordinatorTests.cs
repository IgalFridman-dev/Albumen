using System.Collections.Immutable;
using PhotoSift.Application.Action;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Action;
using Xunit;

namespace PhotoSift.Application.Tests.ActionExecution;

public sealed class ActionRecoveryCoordinatorTests
{
    [Fact]
    public async Task LoadProjectsJournalWithoutCallingProbe()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ActionRecoveryStatus.Ready, fixture.Owner.Current.Status);
        Assert.Equal(ActionRecordEvidenceState.Unknown, Assert.Single(Assert.Single(fixture.Owner.Current.Records).Items).State);
        Assert.Equal(0, fixture.Probe.Calls);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableLoadPublishesFaultedEmptySnapshot()
    {
        var fixture = new RecoveryFixture([], ActionRecordLoadOutcome.Unavailable);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ActionRecoveryStatus.Faulted, fixture.Owner.Current.Status);
        Assert.Empty(fixture.Owner.Current.Records);
        Assert.Equal(0, fixture.Probe.Calls);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitCommandSkipsTerminalRowsAndProbesUnknownRowOnly()
    {
        var terminal = await ItemAsync(RecycleBinOutcomeKind.Recycled);
        var unknown = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([terminal, unknown]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));

        await fixture.PumpAsync(fixture.Owner.RevalidateUnknownAsync(terminal.RequestId, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Probe.Calls);

        fixture.Probe.Result.TrySetResult(Confirmed(unknown));
        await fixture.PumpAsync(fixture.Owner.RevalidateUnknownAsync(unknown.RequestId, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Probe.Calls);
        await fixture.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCommandProbesOrphanedRequestedAndExecutingRows(bool executing)
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        item = item with { Executing = executing ? item.Executing : null, Terminal = null };
        var fixture = new RecoveryFixture([item]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        fixture.Probe.Result.TrySetResult(Confirmed(item));

        await fixture.PumpAsync(fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken));

        Assert.Equal(1, fixture.Probe.Calls);
        Assert.Equal(ActionRecordEvidenceState.ConfirmedPresent,
            Assert.Single(Assert.Single(fixture.Owner.Current.Records).Items).State);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task OneProbeIsOutstandingAndAnotherRequestCannotAccumulate()
    {
        var first = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var second = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([first, second]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));

        var pending = fixture.Owner.RevalidateUnknownAsync(first.RequestId, TestContext.Current.CancellationToken);
        await fixture.PumpUntilAsync(fixture.Probe.Entered.Task);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.ObservedDeadline);
        var rejected = fixture.Owner.RevalidateUnknownAsync(second.RequestId, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(rejected);
        Assert.Equal(1, fixture.Probe.Calls);
        Assert.True(fixture.Owner.Current.ProbeOutstanding);

        fixture.Probe.Result.TrySetResult(Confirmed(first));
        await fixture.PumpAsync(pending);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task DeadlinePublishesUnknownAndLateCompletionIsIgnoredUntilCallSettles()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        var pending = fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken);
        await fixture.PumpUntilAsync(fixture.Probe.Entered.Task);

        fixture.Deadline.TrySetResult(true);
        await fixture.PumpUntilAsync(fixture.Store.Appended.Task);
        Assert.Equal(ActionRevalidationReason.TimedOut, fixture.Store.Facts.Single().Reason);
        Assert.Equal(ActionRecordEvidenceState.Unknown, Assert.Single(Assert.Single(fixture.Owner.Current.Records).Items).State);
        Assert.True(fixture.Owner.Current.ProbeOutstanding);

        await fixture.PumpAsync(fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Probe.Calls);
        fixture.Probe.Result.TrySetResult(Confirmed(item));
        await fixture.PumpAsync(pending);
        await fixture.PumpUntilAsync(() => !fixture.Owner.Current.ProbeOutstanding);
        Assert.Single(fixture.Store.Facts);
        Assert.False(fixture.Owner.Current.ProbeOutstanding);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellationPersistsUnknownRatherThanPublishingProbeSuccess()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        using var caller = new CancellationTokenSource();
        var pending = fixture.Owner.RevalidateUnknownAsync(item.RequestId, caller.Token);
        await fixture.PumpUntilAsync(fixture.Probe.Entered.Task);

        caller.Cancel();
        await fixture.PumpUntilAsync(fixture.Store.Appended.Task);
        Assert.Equal(ActionRevalidationKind.StillUnknown, fixture.Store.Facts.Single().Kind);
        Assert.Equal(ActionRevalidationReason.Failed, fixture.Store.Facts.Single().Reason);
        fixture.Probe.Result.TrySetResult(Confirmed(item));
        await fixture.PumpAsync(pending);
        Assert.Single(fixture.Store.Facts);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ResolvedStateIsPublishedOnlyAfterAppendSucceeds()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]) { BlockAppend = true };
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        fixture.Probe.Result.TrySetResult(Confirmed(item));
        var pending = fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken);
        await fixture.PumpUntilAsync(fixture.Store.AppendEntered.Task);

        Assert.Equal(ActionRecordEvidenceState.Unknown, Assert.Single(Assert.Single(fixture.Owner.Current.Records).Items).State);
        fixture.Store.ReleaseAppend.TrySetResult(true);
        await fixture.PumpAsync(pending);
        Assert.Equal(ActionRecordEvidenceState.ConfirmedPresent, Assert.Single(Assert.Single(fixture.Owner.Current.Records).Items).State);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task StoreFailureLeavesTheItemUnknown()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]);
        fixture.Store.WriteOutcome = ActionRevalidationWriteOutcome.Unavailable;
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        fixture.Probe.Result.TrySetResult(Confirmed(item));

        await fixture.PumpAsync(fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken));

        Assert.Equal(ActionRecordEvidenceState.Unknown, Assert.Single(Assert.Single(fixture.Owner.Current.Records).Items).State);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task DisposeCompletesWithoutWaitingForStuckProbeAndStopsLatePublication()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        _ = fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken);
        await fixture.PumpUntilAsync(fixture.Probe.Entered.Task);
        var before = fixture.Owner.Current;

        await fixture.PumpAsync(fixture.Owner.DisposeAsync().AsTask());
        fixture.Probe.Result.TrySetResult(Confirmed(item));
        await Task.Delay(10, TestContext.Current.CancellationToken);
        while (fixture.Scheduler.TryRunOne()) { }

        Assert.Same(before, fixture.Owner.Current);
        Assert.Empty(fixture.Store.Facts);
    }

    [Fact]
    public async Task DisposeBeforeDeadlineStopsTimeoutPersistence()
    {
        var item = await ItemAsync(RecycleBinOutcomeKind.UnknownPendingRevalidation);
        var fixture = new RecoveryFixture([item]);
        await fixture.PumpAsync(fixture.Owner.LoadAsync(TestContext.Current.CancellationToken));
        var pending = fixture.Owner.RevalidateUnknownAsync(item.RequestId, TestContext.Current.CancellationToken);
        await fixture.PumpUntilAsync(fixture.Probe.Entered.Task);

        await fixture.PumpAsync(fixture.Owner.DisposeAsync().AsTask());
        fixture.Deadline.TrySetResult(true);
        await fixture.PumpAsync(pending);

        Assert.Empty(fixture.Store.Facts);
    }

    private static ActionRevalidationProbeResult Confirmed(PersistedActionItem item)
    {
        var reviewed = item.Requested.Safety.Item;
        return new(ActionRevalidationKind.ConfirmedPresent, ActionRevalidationReason.OriginalFileConfirmed,
            new(true, reviewed.PhysicalFileKey, reviewed.Content.ByteLength, reviewed.Content.Sha256));
    }

    private static async Task<PersistedActionItem> ItemAsync(RecycleBinOutcomeKind outcome)
    {
        await using var fixture = new ActionFixture(1) { ResultFor = _ => outcome switch
        {
            RecycleBinOutcomeKind.Recycled => ActionFixture.Recycled,
            RecycleBinOutcomeKind.NotRemoved => ActionFixture.NotRemoved,
            _ => ActionFixture.Unknown,
        } };
        await fixture.ApproveAsync();
        return new(fixture.Request.RequestId, Assert.Single(fixture.RequestedFacts),
            Assert.Single(fixture.ExecutingFacts), Assert.Single(fixture.TerminalFacts), []);
    }

    private sealed class RecoveryFixture
    {
        internal ManualScanScheduler Scheduler { get; } = new();
        internal RecordStore Store { get; }
        internal Probe Probe { get; } = new();
        internal TaskCompletionSource<bool> Deadline { get; } = ScanTestData.Signal<bool>();
        internal TimeSpan? ObservedDeadline { get; private set; }
        internal ActionRecoveryCoordinator Owner { get; }
        internal bool BlockAppend { set => Store.BlockAppend = value; }

        internal RecoveryFixture(ImmutableArray<PersistedActionItem> items,
            ActionRecordLoadOutcome outcome = ActionRecordLoadOutcome.Loaded)
        {
            Store = new(new(outcome, items));
            Owner = new(Scheduler, Store, Probe, (duration, _) =>
                {
                    ObservedDeadline = duration;
                    return Deadline.Task;
                }, () => DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(30), resultCapacity: 1);
        }

        internal Task PumpAsync(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));
        internal Task PumpUntilAsync(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));
        internal Task PumpUntilAsync(Func<bool> predicate) => ScanTestData.Bound(PumpUntilCoreAsync(predicate));
        internal async ValueTask DisposeAsync() => await PumpAsync(Owner.DisposeAsync().AsTask());

        private async Task PumpUntilCoreAsync(Func<bool> predicate)
        {
            while (!predicate())
                if (!Scheduler.TryRunOne()) await Task.Yield();
        }
    }

    private sealed class RecordStore(ActionRecordLoadResult load) : IActionRecordStore
    {
        internal List<ActionRevalidationFact> Facts { get; } = [];
        internal TaskCompletionSource<bool> Appended { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<bool> AppendEntered { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<bool> ReleaseAppend { get; } = ScanTestData.Signal<bool>();
        internal ActionRevalidationWriteOutcome WriteOutcome { get; set; } = ActionRevalidationWriteOutcome.Persisted;
        internal bool BlockAppend { get; set; }
        public ValueTask<ActionRecordLoadResult> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(load);
        public async ValueTask<ActionRevalidationWriteResult> AppendRevalidationAsync(ActionRevalidationFact fact,
            CancellationToken cancellationToken)
        {
            AppendEntered.TrySetResult(true);
            if (BlockAppend) await ReleaseAppend.Task;
            Facts.Add(fact);
            Appended.TrySetResult(true);
            return new(WriteOutcome);
        }
    }

    private sealed class Probe : IActionRevalidationProbe
    {
        internal int Calls { get; private set; }
        internal TaskCompletionSource<bool> Entered { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<ActionRevalidationProbeResult> Result { get; } = ScanTestData.Signal<ActionRevalidationProbeResult>();
        public async ValueTask<ActionRevalidationProbeResult> ProbeAsync(ActionRevalidationRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult(true);
            return await Result.Task;
        }
    }
}
