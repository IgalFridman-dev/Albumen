using PhotoSift.Application.Action;
using PhotoSift.Application.Tests.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.ActionExecution;

public sealed class ActionOwnerInboxTests
{
    [Fact]
    public async Task FullResultLaneCannotBlockCommandsOrFaultReporting()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ActionOwnerInbox(scheduler, 1);
        var first = inbox.ResultAsync(() => 1);
        var waiting = inbox.ResultAsync(() => 2);
        var repeated = inbox.CommandAsync(() => 3);
        var fault = inbox.CommandAsync(() => 4);
        scheduler.RunOne();
        scheduler.RunOne();
        scheduler.RunOne();
        Assert.Equal(3, await ScanTestData.Bound(repeated));
        Assert.Equal(4, await ScanTestData.Bound(fault));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(waiting));
        Assert.Equal(1, await first);
        Assert.Equal(2, await waiting);
    }

    [Fact]
    public async Task TurnsAndPublicationsCannotNestAndFaultsReturnCapacity()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ActionOwnerInbox(scheduler, 1);
        var order = new List<int>();
        Task<int>? nested = null;
        inbox.DeliverAfterTurn = () =>
        {
            Assert.False(scheduler.TryRunOne());
            order.Add(2);
        };
        var failed = inbox.ResultAsync<int>(() =>
        {
            order.Add(1);
            nested = inbox.CommandAsync(() => 7);
            Assert.False(scheduler.TryRunOne());
            throw new InvalidOperationException("injected turn fault");
        });
        Assert.Empty(order);
        scheduler.RunOne();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(failed));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(nested!));
        var next = inbox.ResultAsync(() => 9);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(next));
        Assert.Equal(9, await next);
        Assert.Equal([1, 2, 2], order);
    }
}
