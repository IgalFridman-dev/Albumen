using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class BoundedReviewRevalidatorTests
{
    // A blocked owner acknowledgment must bound protected opens and never hold a command turn.
    [Fact]
    public async Task FullResultLaneBoundsWorkersWhileCancellationAndStaleAcknowledgmentsDrain()
    {
        var context = ScanTestData.Context();
        var session = new ReviewSessionPin(Guid.NewGuid(), context.Pin);
        var hashed = ScanTestData.Signal<bool>();
        var release = ScanTestData.Signal<bool>();
        var count = 0;
        var reader = new ScanTestReader();
        var analyzer = new ScanTestAnalyzer { Hash = (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 3) hashed.TrySetResult(true);
            return ValueTask.FromResult(new StreamDigest(1, ScanTestData.Digest));
        } };
        var sink = new ReviewSink { Commit = _ => new(release.Task), Failure = (_, _) => new(release.Task) };
        var run = new BoundedReviewRevalidator(reader, analyzer, 3, 1).Start(session, context, ReviewData.Groups(context, 100), sink);
        try
        {
            await ScanTestData.Bound(hashed.Task);
            await ScanTestData.Bound(sink.Submitted.Task);
            Assert.Equal(3, reader.Live);
            Assert.Single(sink.Evidence);
            Assert.False(run.Completion.IsCompleted);
            await ScanTestData.Bound(run.CancelAsync());
            Assert.Equal(3, reader.Live);
            release.SetResult(false);
            await ScanTestData.Bound(run.Completion);
            Assert.Equal(0, reader.Live);
            Assert.Equal(3, count);
            Assert.All(sink.Evidence, item => { Assert.Same(session, item.Request.Session); Assert.Same(context, item.Request.Context); Assert.Equal(3, item.Request.GroupRevision); Assert.Equal(1, item.Request.Attempt); });
            Assert.All(sink.Failures, item => Assert.Equal(ReviewUnavailableReason.Canceled, item.Reason));
            Assert.InRange(sink.Evidence.Count + sink.Failures.Count, 3, 5);
        }
        finally { release.TrySetResult(false); await ScanTestData.Bound(run.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task WorkerFaultCancelsBlockedProducerAndDrainsEveryAdmittedResult()
    {
        var context = ScanTestData.Context();
        var hash = ScanTestData.Signal<StreamDigest>();
        var entered = ScanTestData.Signal<bool>();
        var reader = new ScanTestReader();
        var analyzer = new ScanTestAnalyzer { Hash = (_, _) => { entered.TrySetResult(true); return new(hash.Task); } };
        var sink = new ReviewSink();
        var run = new BoundedReviewRevalidator(reader, analyzer, 1, 1).Start(new(Guid.NewGuid(), context.Pin), context, ReviewData.Groups(context, 100), sink);
        await ScanTestData.Bound(entered.Task);
        var bug = new InvalidOperationException("hash bug");
        hash.SetException(bug);
        Assert.Same(bug, await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(run.Completion)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(run.DisposeAsync().AsTask()));
        Assert.Empty(sink.Evidence);
        Assert.Equal(1, sink.Failures.Count(item => item.Reason == ReviewUnavailableReason.ProgrammingFault));
        Assert.All(sink.Failures.Where(item => item.Reason != ReviewUnavailableReason.ProgrammingFault), item => Assert.Equal(ReviewUnavailableReason.Canceled, item.Reason));
        Assert.InRange(sink.Failures.Count, 1, 3);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task OwnerFaultSettlesItsAcknowledgmentAndDoesNotStrandTheResultDrain()
    {
        var context = ScanTestData.Context();
        var ack = ScanTestData.Signal<bool>();
        var sink = new ReviewSink { Commit = _ => new(ack.Task) };
        var reader = new ScanTestReader();
        var run = new BoundedReviewRevalidator(reader, new ScanTestAnalyzer(), 3, 1)
            .Start(new(Guid.NewGuid(), context.Pin), context, ReviewData.Groups(context, 100), sink);
        await ScanTestData.Bound(sink.Submitted.Task);
        ack.SetException(new InvalidOperationException("owner bug"));
        var fault = await Record.ExceptionAsync(() => ScanTestData.Bound(run.Completion));
        Assert.NotNull(fault);
        Assert.IsNotType<TimeoutException>(fault);
        Assert.Contains("owner bug", fault.ToString(), StringComparison.Ordinal);
        var disposalFault = await Record.ExceptionAsync(() => ScanTestData.Bound(run.DisposeAsync().AsTask()));
        Assert.NotNull(disposalFault);
        Assert.IsNotType<TimeoutException>(disposalFault);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalObservesCancellationCallbacksAfterWorkSettlement(bool callbackFault)
    {
        using var callbackRelease = new ManualResetEventSlim();
        var callbackStarted = ScanTestData.Signal<bool>();
        var ack = ScanTestData.Signal<bool>();
        var registration = default(CancellationTokenRegistration);
        var analyzer = new ScanTestAnalyzer { Hash = (_, token) =>
        {
            registration = token.Register(() =>
            {
                callbackStarted.TrySetResult(true);
                Assert.True(callbackRelease.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                if (callbackFault) throw new InvalidOperationException("callback bug");
            });
            return ValueTask.FromResult(new StreamDigest(1, ScanTestData.Digest));
        } };
        var context = ScanTestData.Context();
        var sink = new ReviewSink { Commit = _ => new(ack.Task) };
        var run = new BoundedReviewRevalidator(new ScanTestReader(), analyzer, 1, 1)
            .Start(new(Guid.NewGuid(), context.Pin), context, ReviewData.Groups(context, 1), sink);
        try
        {
            await ScanTestData.Bound(sink.Submitted.Task);
            var cancel = run.CancelAsync();
            await ScanTestData.Bound(callbackStarted.Task);
            var disposal = run.DisposeAsync().AsTask();
            ack.SetResult(false);
            await ScanTestData.Bound(run.Completion);
            Assert.False(disposal.IsCompleted);
            callbackRelease.Set();
            if (callbackFault)
            {
                await Assert.ThrowsAsync<AggregateException>(() => ScanTestData.Bound(cancel));
                await Assert.ThrowsAsync<AggregateException>(() => ScanTestData.Bound(disposal));
            }
            else
            {
                await ScanTestData.Bound(cancel);
                await ScanTestData.Bound(disposal);
                await run.DisposeAsync();
                await run.CancelAsync();
            }
        }
        finally { callbackRelease.Set(); ack.TrySetResult(false); await registration.DisposeAsync(); }
    }

    [Fact]
    public async Task ReplacementCommandCanPassAFullWorkerLaneBeforeStaleAcknowledgment()
    {
        var context = ScanTestData.Context();
        var session = new ReviewSessionPin(Guid.NewGuid(), context.Pin);
        var scheduler = new ManualScanScheduler();
        var owner = new ScanOwnerInbox(scheduler, 1);
        var current = session;
        var submitted = ScanTestData.Signal<bool>();
        var accepted = 0;
        var sink = new ReviewSink { Commit = evidence =>
        {
            var result = owner.ResultAsync(() => { if (evidence.Request.Session != current) return false; accepted++; return true; });
            submitted.TrySetResult(true);
            return new(result);
        } };
        var replacement = owner.CommandAsync(() => current = new(Guid.NewGuid(), context.Pin));
        var reader = new ScanTestReader();
        var run = new BoundedReviewRevalidator(reader, new ScanTestAnalyzer(), 3, 1).Start(session, context, ReviewData.Groups(context, 20), sink);
        await ScanTestData.Bound(submitted.Task);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(replacement));
        await run.CancelAsync();
        await ScanTestData.Bound(scheduler.PumpUntilAsync(run.Completion));
        await run.DisposeAsync();
        Assert.Equal(0, accepted);
        Assert.Equal(0, reader.Live);
    }
}
