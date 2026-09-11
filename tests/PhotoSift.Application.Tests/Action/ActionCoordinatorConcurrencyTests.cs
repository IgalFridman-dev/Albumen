using PhotoSift.Application.Action;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Action;
using Xunit;

namespace PhotoSift.Application.Tests.ActionExecution;

public sealed class ActionCoordinatorConcurrencyTests
{
    [Fact]
    public async Task FinalPublicationAndItsFaultAcknowledgmentDrainBeforeCompletionAndDisposal()
    {
        await using var fixture = new ActionFixture(1) { GateAt = "execute:1" };
        var finalEntered = ScanTestData.Signal<bool>();
        var releaseFinal = ScanTestData.Signal<bool>();
        var faultEntered = ScanTestData.Signal<bool>();
        var releaseFault = ScanTestData.Signal<bool>();
        var faultPublished = ScanTestData.Signal<bool>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == ActionStatus.Complete)
            {
                finalEntered.TrySetResult(true);
                releaseFinal.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("final-only observer failure");
            }
            if (snapshot.Status == ActionStatus.Faulted)
            {
                faultEntered.TrySetResult(true);
                releaseFault.Task.GetAwaiter().GetResult();
                faultPublished.TrySetResult(true);
            }
        };
        var approval = fixture.Owner.ApproveAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(fixture.Entered.Task);
        var disposal = fixture.Owner.DisposeAsync().AsTask();
        await fixture.PumpAsync(fixture.CancellationObserved.Task);
        fixture.Release.TrySetResult(true);
        // An independent scheduler pump lets the test hold each synchronous
        // observer open without sleeping or blocking the assertion thread.
        var pump = Task.Run(() => fixture.PumpAsync(faultPublished.Task), CancellationToken.None);
        bool prematureFinalCompletion;
        bool prematureFaultCompletion;
        try
        {
            await ScanTestData.Bound(finalEntered.Task);
            prematureFinalCompletion = approval.IsCompleted || disposal.IsCompleted;
            releaseFinal.TrySetResult(true);
            await ScanTestData.Bound(faultEntered.Task);
            prematureFaultCompletion = approval.IsCompleted || disposal.IsCompleted;
        }
        finally
        {
            releaseFinal.TrySetResult(true);
            releaseFault.TrySetResult(true);
        }
        await ScanTestData.Bound(pump);
        await fixture.PumpAsync(Task.WhenAll(approval, disposal));
        Assert.Equal(ActionCommandOutcome.Faulted, (await approval).Outcome);
        Assert.False(prematureFinalCompletion);
        Assert.False(prematureFaultCompletion);
        Assert.Same(fixture.Owner.Current, (await approval).Snapshot);
        Assert.Equal(ActionItemState.Recycled, (await approval).Snapshot.Items[0].State);
        Assert.False(fixture.Scheduler.TryRunOne());
    }

    [Theory]
    [InlineData("claim", ActionItemState.NotRequested, 0, 0)]
    [InlineData("refresh:1", ActionItemState.NotRequested, 0, 0)]
    [InlineData("requested:1", ActionItemState.InterruptedBeforeExecution, 1, 0)]
    [InlineData("executing:1", ActionItemState.UnknownPendingRevalidation, 1, 1)]
    [InlineData("execute:1", ActionItemState.Recycled, 1, 1)]
    [InlineData("terminal:1", ActionItemState.Recycled, 1, 1)]
    public async Task CancellationSettlesAdmittedEvidenceAndNeverStartsTheNextItem(string phase,
        ActionItemState state, long requested, long executing)
    {
        await using var fixture = new ActionFixture(2) { GateAt = phase };
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Owner.ApproveAsync(fixture.Request, cancellation.Token);
        await fixture.PumpAsync(fixture.Entered.Task);
        cancellation.Cancel();
        if (phase is "claim" or "refresh:1" or "execute:1")
            await ScanTestData.Bound(fixture.CancellationObserved.Task);
        else Assert.False(fixture.GatedToken.CanBeCanceled);
        var repeated = await fixture.ApproveAsync();
        Assert.Equal(ActionCommandOutcome.Existing, repeated.Outcome);
        Assert.False(first.IsCompleted);
        fixture.Release.TrySetResult(true);
        await fixture.PumpAsync(first);
        var snapshot = (await first).Snapshot;
        Assert.Equal(state, snapshot.Items[0].State);
        Assert.Equal(ActionItemState.NotRequested, snapshot.Items[1].State);
        Assert.Equal(requested, snapshot.DurableRequestedCount);
        Assert.Equal(executing, snapshot.DurableExecutingCount);
        Assert.DoesNotContain("execute:2", fixture.Calls);
        if (phase == "executing:1") Assert.DoesNotContain("execute:1", fixture.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutorOwnsPermissionBoundaryWhileOwnerAlwaysDrainsTerminalAndDisposal(bool permissionGranted)
    {
        await using var fixture = new ActionFixture(1) { GateAt = "execute:1" };
        fixture.ResultFor = _ => permissionGranted ? ActionFixture.Recycled : ActionFixture.NotRemoved;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Owner.ApproveAsync(fixture.Request, cancellation.Token);
        await fixture.PumpAsync(fixture.Entered.Task);
        var disposal = fixture.Owner.DisposeAsync().AsTask();
        await fixture.PumpAsync(fixture.CancellationObserved.Task);
        Assert.False(disposal.IsCompleted);
        cancellation.Cancel();
        fixture.Release.TrySetResult(true);
        await fixture.PumpAsync(disposal);
        await fixture.PumpAsync(pending);
        Assert.Contains("terminal:1", fixture.Calls);
        Assert.Equal(permissionGranted ? ActionItemState.Recycled : ActionItemState.NotRemoved,
            (await pending).Snapshot.Items[0].State);
    }

    [Fact]
    public async Task ObserverFaultIsVisibleOrderedAndCannotOverwriteAnAdmittedTerminal()
    {
        await using var fixture = new ActionFixture(2) { GateAt = "execute:1" };
        var publications = new List<ActionSnapshot>();
        fixture.Owner.SnapshotChanged += (_, snapshot) => publications.Add(snapshot);
        var first = fixture.Owner.ApproveAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(fixture.Entered.Task);
        fixture.Owner.SnapshotChanged += (_, _) => throw new InvalidOperationException("observer failed");
        await fixture.PumpAsync(fixture.Owner.ReportObserverFaultAsync());
        Assert.Equal(ActionStatus.Faulted, fixture.Owner.Current.Status);
        Assert.Equal(ActionCommandOutcome.Existing, (await fixture.ApproveAsync()).Outcome);
        fixture.Release.TrySetResult(true);
        await fixture.PumpAsync(first);
        Assert.Equal(ActionStatus.Faulted, (await first).Snapshot.Status);
        Assert.Equal(ActionItemState.Recycled, (await first).Snapshot.Items[0].State);
        Assert.Equal(ActionItemState.NotRequested, (await first).Snapshot.Items[1].State);
        Assert.Equal(ActionItemState.Waiting, publications[0].Items[0].State);
        Assert.Contains(publications, snapshot => snapshot.Items[0].State == ActionItemState.Requested);
        Assert.Contains(publications, snapshot => snapshot.Items[0].State == ActionItemState.Executing);
        Assert.Equal(fixture.Owner.Current, publications[^1]);
    }

    [Fact]
    public async Task ThrowingObserverReportsFaultWithoutNestingDataTurns()
    {
        await using var fixture = new ActionFixture(1);
        fixture.Owner.SnapshotChanged += (_, _) =>
        {
            Assert.False(fixture.Scheduler.TryRunOne());
            throw new InvalidOperationException("observer failure");
        };
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionStatus.Faulted, result.Snapshot.Status);
        Assert.Equal(ActionSafetyReason.ProgrammingFault, result.Snapshot.FaultReason);
        Assert.Empty(fixture.RequestedFacts);
    }

    [Fact]
    public async Task CancellationCallbacksRunOutsideOwnerAndDrainBeforeDisposalCompletes()
    {
        await using var fixture = new ActionFixture(1) { GateAt = "claim" };
        var callbackEntered = ScanTestData.Signal<bool>();
        var callbackRelease = ScanTestData.Signal<bool>();
        CancellationTokenRegistration registration = default;
        fixture.OnCall = (phase, token) =>
        {
            if (phase == "claim") registration = token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                callbackRelease.Task.GetAwaiter().GetResult();
            });
            return Task.CompletedTask;
        };
        var pending = fixture.Owner.ApproveAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(fixture.Entered.Task);
        var disposal = fixture.Owner.DisposeAsync().AsTask();
        try
        {
            await fixture.PumpAsync(callbackEntered.Task);
            await fixture.PumpAsync(fixture.Owner.ReportObserverFaultAsync());
            fixture.Release.TrySetResult(true);
            Assert.False(disposal.IsCompleted);
        }
        finally { callbackRelease.TrySetResult(true); }
        await fixture.PumpAsync(disposal);
        await fixture.PumpAsync(pending);
        await registration.DisposeAsync();
    }
}
