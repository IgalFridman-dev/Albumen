using PhotoSift.Application.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ScanOwnerInboxTests
{
    [Fact]
    public async Task SaturatedResultsDoNotBlockCommandEntry()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ScanOwnerInbox(scheduler, resultCapacity: 1);
        var first = inbox.ResultAsync(() => 1);
        var waiting = inbox.ResultAsync(() => 2);
        var command = inbox.CommandAsync(() => 9);
        Assert.False(waiting.IsCompleted);
        scheduler.RunOne();
        scheduler.RunOne();
        Assert.Equal(9, await ScanTestData.Bound(command));
        Assert.Equal(1, await ScanTestData.Bound(first));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(waiting));
        Assert.Equal(2, await waiting);
    }

    [Fact]
    public async Task TurnsAreFifoAndObserverReentryCannotNestEvenWhenSchedulerIsPumped()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ScanOwnerInbox(scheduler);
        var order = new List<int>();
        Task<int>? reentry = null;
        var first = inbox.CommandAsync(() =>
        {
            order.Add(1);
            reentry = inbox.CommandAsync(() => { order.Add(3); return 3; });
            Assert.False(scheduler.TryRunOne());
            order.Add(2);
            return 1;
        });
        var second = inbox.ResultAsync(() => { order.Add(4); return 4; });
        Assert.Empty(order);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(second));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(reentry!));
        await first;
        Assert.Equal([1, 2, 4, 3], order);
    }

    [Fact]
    public async Task ExceptionsSettleCallersAndReturnResultCapacity()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ScanOwnerInbox(scheduler, 1);
        var result = inbox.ResultAsync<int>(() => throw new InvalidOperationException("result"));
        var command = inbox.CommandAsync<int>(() => throw new InvalidOperationException("command"));
        var next = inbox.ResultAsync(() => 7);
        scheduler.RunOne();
        scheduler.RunOne();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(result));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(command));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(next));
        Assert.Equal(7, await next);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveCapacityIsRejected(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOwnerInbox(new ManualScanScheduler(), capacity));
}
