using PhotoSift.Application.Review;
using PhotoSift.Application.Tests.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ReviewOwnerInboxTests
{
    [Fact]
    public async Task FullResultLaneLeavesCommandsFreeAndSettlesEveryAcknowledgment()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ReviewOwnerInbox(scheduler, 1);
        var first = inbox.ResultAsync(() => 1);
        var waiting = inbox.ResultAsync(() => 2);
        var command = inbox.CommandAsync(() => 9);
        Assert.False(waiting.IsCompleted);
        scheduler.RunOne();
        scheduler.RunOne();
        Assert.Equal(9, await ScanTestData.Bound(command));
        Assert.Equal(1, await first);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(waiting));
        Assert.Equal(2, await waiting);
    }

    [Fact]
    public async Task FifoCommandsCannotRunInlineOrNestThroughAPumpedScheduler()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ReviewOwnerInbox(scheduler);
        var order = new List<int>();
        Task<int>? nested = null;
        var first = inbox.CommandAsync(() =>
        {
            order.Add(1);
            nested = inbox.CommandAsync(() => { order.Add(4); return 4; });
            Assert.False(scheduler.TryRunOne());
            order.Add(2);
            return 1;
        });
        var second = inbox.ResultAsync(() => { order.Add(3); return 3; });
        Assert.Empty(order);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(second));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(nested!));
        await first;
        Assert.Equal([1, 2, 3, 4], order);
    }

    [Fact]
    public async Task FaultedResultReturnsCapacityAndDoesNotStrandCommands()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ReviewOwnerInbox(scheduler, 1);
        var failed = inbox.ResultAsync<int>(() => throw new InvalidOperationException("failure"));
        var next = inbox.ResultAsync(() => 3);
        scheduler.RunOne();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(failed));
        await ScanTestData.Bound(scheduler.PumpUntilAsync(next));
        Assert.Equal(3, await next);
    }
}
