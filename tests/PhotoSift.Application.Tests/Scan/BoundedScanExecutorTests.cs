using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class BoundedScanExecutorTests
{
    [Fact]
    public async Task CancellationDrainsQueuedAndRegisteredBlockedRowWithOnlyOneWorker()
    {
        var full = ScanTestData.Signal<bool>();
        var hashing = ScanTestData.Signal<bool>();
        var reader = new ScanTestReader();
        var registered = 0;
        var sink = new ScanTestSink
        {
            Register = (context, file) =>
            {
                var id = Interlocked.Increment(ref registered);
                if (id == 34) full.SetResult(true);
                return ValueTask.FromResult<OccurrenceWork?>(new(context, new OccurrenceToken(context.Pin, id, 1, 1), file));
            },
        };
        var analyzer = new ScanTestAnalyzer
        {
            Hash = async (_, token) =>
            {
                hashing.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        };
        var run = new BoundedScanExecutor(new ScanTestEnumerator(100), reader, analyzer).Start(ScanTestData.Context(), sink);
        await ScanTestData.Bound(hashing.Task);
        await ScanTestData.Bound(full.Task);
        await ScanTestData.Bound(run.CancelAsync());
        await ScanTestData.Bound(run.Completion);
        await ScanTestData.Bound(run.DisposeAsync().AsTask());
        Assert.Equal(34, registered);
        Assert.Equal(34, sink.Failures.Count);
        Assert.Equal(34, sink.Failures.Select(failure => failure.Token).Distinct().Count());
        Assert.All(sink.Failures, failure => Assert.Equal(ScanTestData.Canceled, failure.Outcome));
        Assert.Equal(ScanTestData.Canceled, Assert.Single(sink.Endings));
        Assert.Equal(1, reader.MaximumLive);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task NormalCompletionAwaitsEveryAcceptedResult()
    {
        var submitted = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<SignatureCommit>();
        var reader = new ScanTestReader();
        var count = 0;
        var sink = new ScanTestSink
        {
            Signature = _ =>
            {
                if (Interlocked.Increment(ref count) == 3) { submitted.SetResult(true); return new(acknowledge.Task); }
                return ValueTask.FromResult(new SignatureCommit(true, []));
            },
        };
        var run = new BoundedScanExecutor(new ScanTestEnumerator(3), reader, new ScanTestAnalyzer()).Start(ScanTestData.Context(), sink);
        await ScanTestData.Bound(submitted.Task);
        Assert.False(run.Completion.IsCompleted);
        Assert.Equal(1, reader.Live);
        acknowledge.SetResult(new SignatureCommit(false, []));
        await ScanTestData.Bound(run.Completion);
        await ScanTestData.Bound(run.DisposeAsync().AsTask());
        Assert.Equal(3, count);
        Assert.Equal(ScanTestData.Success, Assert.Single(sink.Endings));
        Assert.Empty(sink.Failures);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingEndOrEnumerationReadFailureIsReportedAsIncomplete(bool readFailure)
    {
        var enumerator = new ScanTestEnumerator(1) { IncludeEnd = false,
            EndException = readFailure ? new IOException("enumeration failed") : null };
        var sink = new ScanTestSink();
        var run = new BoundedScanExecutor(enumerator, new ScanTestReader(), new ScanTestAnalyzer()).Start(ScanTestData.Context(), sink);
        await ScanTestData.Bound(run.Completion);
        await ScanTestData.Bound(run.DisposeAsync().AsTask());
        Assert.Equal(ScanReason.IncompleteEnumeration, Assert.Single(sink.Endings).Reason);
    }

    [Fact]
    public async Task WorkerProgrammingFaultCancelsProducerAndReconcilesAcceptedRows()
    {
        var full = ScanTestData.Signal<bool>();
        var fault = ScanTestData.Signal<StreamDigest>();
        var registered = 0;
        var sink = new ScanTestSink
        {
            Register = (context, file) =>
            {
                var id = Interlocked.Increment(ref registered);
                if (id == 34) full.SetResult(true);
                return ValueTask.FromResult<OccurrenceWork?>(new(context, new OccurrenceToken(context.Pin, id, 1, 1), file));
            },
        };
        var reader = new ScanTestReader();
        var run = new BoundedScanExecutor(new ScanTestEnumerator(100), reader,
            new ScanTestAnalyzer { Hash = (_, _) => new(fault.Task) }).Start(ScanTestData.Context(), sink);
        await ScanTestData.Bound(full.Task);
        fault.SetException(new InvalidOperationException("analyzer bug"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(run.Completion));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(run.DisposeAsync().AsTask()));
        Assert.Equal(34, registered);
        Assert.Equal(34, sink.Failures.Select(failure => failure.Token).Distinct().Count());
        Assert.Contains(sink.Failures, failure => failure.Token.OccurrenceId == 1 && failure.Outcome.Reason == ScanReason.ProgrammingFault);
        Assert.Equal(0, reader.Live);
        Assert.Single(sink.Endings);
    }

    [Fact]
    public async Task DisposalWaitsForCancellationCallbacksAndWorkAndIsIdempotent()
    {
        using var releaseCallback = new ManualResetEventSlim();
        var started = ScanTestData.Signal<bool>();
        var callbackStarted = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<SignatureCommit>();
        var registration = default(CancellationTokenRegistration);
        var analyzer = new ScanTestAnalyzer
        {
            Hash = (_, token) =>
            {
                registration = token.Register(() =>
                {
                    callbackStarted.SetResult(true);
                    Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                });
                return ValueTask.FromResult(new StreamDigest(1, ScanTestData.Digest));
            },
        };
        var sink = new ScanTestSink { Signature = _ => { started.SetResult(true); return new(acknowledge.Task); } };
        var run = new BoundedScanExecutor(new ScanTestEnumerator(1), new ScanTestReader(), analyzer).Start(ScanTestData.Context(), sink);
        try
        {
            await ScanTestData.Bound(started.Task);
            var cancel = run.CancelAsync();
            await ScanTestData.Bound(callbackStarted.Task);
            var dispose = run.DisposeAsync().AsTask();
            Assert.False(cancel.IsCompleted);
            Assert.False(dispose.IsCompleted);
            acknowledge.SetResult(new SignatureCommit(false, []));
            await ScanTestData.Bound(run.Completion);
            Assert.False(dispose.IsCompleted);
            releaseCallback.Set();
            await ScanTestData.Bound(cancel);
            await ScanTestData.Bound(dispose);
            await ScanTestData.Bound(run.DisposeAsync().AsTask());
            await ScanTestData.Bound(run.CancelAsync());
        }
        finally
        {
            releaseCallback.Set();
            acknowledge.TrySetResult(new SignatureCommit(false, []));
            await registration.DisposeAsync();
        }
    }
}
