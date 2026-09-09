using System.Collections.Concurrent;
using System.Collections.Immutable;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ProtectedReviewWorkerTests
{
    // These tests catch early lease disposal and cancellation of admitted acknowledgments.
    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("stale")]
    [InlineData("faulted")]
    public async Task LeaseSurvivesEveryPendingAcknowledgmentAndWorkerCancellation(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var ack = ScanTestData.Signal<bool>();
        var reader = new ScanTestReader();
        var request = ReviewData.Request();
        var sink = new ReviewSink { Commit = _ => new(ack.Task) };
        var work = new ProtectedReviewWorker(reader, new ScanTestAnalyzer()).RevalidateAsync(request, sink, cancellation.Token);
        await ScanTestData.Bound(sink.Submitted.Task);
        await cancellation.CancelAsync();
        Assert.Equal(1, reader.Live);
        Assert.False(work.IsCompleted);
        Assert.Same(request, Assert.Single(sink.Evidence).Request);
        if (outcome == "faulted")
        {
            ack.SetException(new InvalidOperationException("owner fault"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(work));
        }
        else
        {
            ack.SetResult(outcome == "accepted");
            await ScanTestData.Bound(work);
        }
        Assert.Equal(0, reader.Live);
        Assert.Empty(sink.Failures);
    }

    [Theory]
    [InlineData("opened-key", ReviewUnavailableReason.Changed)]
    [InlineData("opened-length", ReviewUnavailableReason.Changed)]
    [InlineData("opened-path", ReviewUnavailableReason.Changed)]
    [InlineData("final-key", ReviewUnavailableReason.Changed)]
    [InlineData("final-length", ReviewUnavailableReason.Changed)]
    [InlineData("final-path", ReviewUnavailableReason.Changed)]
    [InlineData("escape", ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("digest", ReviewUnavailableReason.Changed)]
    [InlineData("digest-length", ReviewUnavailableReason.Changed)]
    [InlineData("refresh", ReviewUnavailableReason.ReadFailure)]
    public async Task RevisionOrPathMismatchNeverCommitsAndFailureOwnsLease(string mismatch, ReviewUnavailableReason expected)
    {
        var ack = ScanTestData.Signal<bool>();
        var request = ReviewData.Request();
        var reader = new ScanTestReader
        {
            Create = (_, open) =>
            {
                Assert.Same(request.Context, open.Context);
                Assert.Equal(request.Occurrence.DiscoveredPath, open.DiscoveredPath);
                Assert.Equal(request.Occurrence.ResolvedPath, open.ResolvedPath);
                Assert.Equal(request.Occurrence.PhysicalFileKey, open.ExpectedPhysicalFileKey);
                var lease = new ScanTestLease(open);
                if (mismatch.StartsWith("opened-", StringComparison.Ordinal)) lease.OpenedFacts = Alter(lease.OpenedFacts, mismatch[7..]);
                lease.Refresh = _ => mismatch == "refresh"
                    ? ValueTask.FromException<ProtectedFileFacts>(new IOException("refresh failed"))
                    : ValueTask.FromResult(mismatch.StartsWith("final-", StringComparison.Ordinal)
                        ? Alter(lease.OpenedFacts, mismatch[6..])
                        : mismatch == "escape" ? lease.OpenedFacts with { ResolvedPath = @"C:\Other\1.jpg" } : lease.OpenedFacts);
                return lease;
            },
        };
        var analyzer = new ScanTestAnalyzer { Hash = (_, _) => ValueTask.FromResult(new StreamDigest(
            mismatch == "digest-length" ? 2 : 1, mismatch == "digest" ? new string('B', 64) : ScanTestData.Digest)) };
        var sink = new ReviewSink { Failure = (_, _) => new(ack.Task) };
        var work = new ProtectedReviewWorker(reader, analyzer).RevalidateAsync(request, sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(sink.Submitted.Task);
        var failure = Assert.Single(sink.Failures);
        Assert.Same(request, failure.Request);
        Assert.Equal(expected, failure.Reason);
        Assert.Empty(sink.Evidence);
        Assert.Equal(1, reader.Live);
        ack.SetResult(false);
        await ScanTestData.Bound(work);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData("missing", ReviewUnavailableReason.Missing)]
    [InlineData("directory", ReviewUnavailableReason.Missing)]
    [InlineData("access", ReviewUnavailableReason.AccessDenied)]
    [InlineData("sharing", ReviewUnavailableReason.SharingViolation)]
    [InlineData("reparse", ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("sync", ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("placeholder", ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("unknown", ReviewUnavailableReason.UnknownSafety)]
    [InlineData("read", ReviewUnavailableReason.ReadFailure)]
    public async Task KnownReadFailuresPreservePinnedPhysicalKeyForAliasInvalidation(string fault, ReviewUnavailableReason expected)
    {
        var request = ReviewData.Request();
        var reader = new ScanTestReader { Create = (_, _) => throw Failure(fault) };
        var sink = new ReviewSink();
        await new ProtectedReviewWorker(reader, new ScanTestAnalyzer()).RevalidateAsync(request, sink, TestContext.Current.CancellationToken);
        var failure = Assert.Single(sink.Failures);
        Assert.Same(request, failure.Request);
        Assert.Equal(new PhysicalFileKey(1, new FileId128(0, 1)), failure.Request.Occurrence.PhysicalFileKey);
        Assert.Equal(expected, failure.Reason);
        Assert.Empty(sink.Evidence);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(false, "analyzer")]
    [InlineData(true, "analyzer")]
    [InlineData(false, "cancellation")]
    [InlineData(true, "cancellation")]
    [InlineData(false, "typed")]
    [InlineData(true, "typed")]
    public async Task ProgrammingFaultIsAcknowledgedWithLeaseAliveThenSurfaced(bool acknowledgmentFault, string kind)
    {
        Exception bug = kind switch
        {
            "cancellation" => new OperationCanceledException("unrequested cancellation"),
            "typed" => new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault), "reader bug"),
            _ => new InvalidOperationException("analyzer bug"),
        };
        var ack = ScanTestData.Signal<bool>();
        var sink = new ReviewSink { Failure = (_, _) => new(ack.Task) };
        var reader = new ScanTestReader();
        var work = new ProtectedReviewWorker(reader, new ScanTestAnalyzer { Hash = (_, _) => throw bug })
            .RevalidateAsync(ReviewData.Request(), sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(sink.Submitted.Task);
        Assert.Equal(ReviewUnavailableReason.ProgrammingFault, Assert.Single(sink.Failures).Reason);
        Assert.Equal(1, reader.Live);
        if (acknowledgmentFault)
        {
            var owner = new IOException("owner fault");
            ack.SetException(owner);
            var aggregate = await Assert.ThrowsAsync<AggregateException>(() => ScanTestData.Bound(work));
            Assert.Contains(bug, aggregate.InnerExceptions);
            Assert.Contains(owner, aggregate.InnerExceptions);
        }
        else
        {
            ack.SetResult(false);
            Assert.Same(bug, await Record.ExceptionAsync(() => ScanTestData.Bound(work)));
        }
        Assert.Equal(0, reader.Live);
    }

    // A throwing asynchronous cleanup must add evidence, never replace an earlier fault.
    [Theory]
    [InlineData("analyzer")]
    [InlineData("acknowledgment")]
    [InlineData("analyzer-and-acknowledgment")]
    [InlineData("disposal-only")]
    public async Task AsynchronousDisposalPreservesEveryPrimaryFaultAfterAcknowledgment(string scenario)
    {
        var analyzerFault = new InvalidOperationException("analyzer fault");
        var acknowledgmentFault = new ArgumentException("acknowledgment fault");
        var disposalFault = new IOException("disposal fault");
        var failAnalyzer = scenario is "analyzer" or "analyzer-and-acknowledgment";
        var failAcknowledgment = scenario is "acknowledgment" or "analyzer-and-acknowledgment";
        var ack = ScanTestData.Signal<bool>();
        var sink = new ReviewSink { Commit = _ => new(ack.Task), Failure = (_, _) => new(ack.Task) };
        var reader = new DisposalFailureReader(disposalFault);
        var analyzer = new ScanTestAnalyzer { Hash = (_, _) => failAnalyzer
            ? ValueTask.FromException<StreamDigest>(analyzerFault)
            : ValueTask.FromResult(new StreamDigest(1, ScanTestData.Digest)) };
        var work = new ProtectedReviewWorker(reader, analyzer)
            .RevalidateAsync(ReviewData.Request(), sink, TestContext.Current.CancellationToken);
        try
        {
            await ScanTestData.Bound(sink.Submitted.Task);
            var lease = Assert.IsType<DisposalFailureLease>(reader.Lease);
            Assert.True(lease.Content.CanRead);
            Assert.Equal(0, lease.DisposalAttempts);
            Assert.False(work.IsCompleted);
            if (failAnalyzer)
            {
                Assert.Empty(sink.Evidence);
                Assert.Equal(ReviewUnavailableReason.ProgrammingFault, Assert.Single(sink.Failures).Reason);
            }
            else { Assert.Single(sink.Evidence); Assert.Empty(sink.Failures); }

            if (failAcknowledgment) ack.SetException(acknowledgmentFault);
            else ack.SetResult(true);
            await ScanTestData.Bound(lease.DisposalStarted.Task);
            Assert.Equal(1, lease.DisposalAttempts);
            Assert.True(lease.Content.CanRead);
            Assert.False(work.IsCompleted);
            lease.ReleaseDisposal.SetResult(true);
            var observed = await Record.ExceptionAsync(() => ScanTestData.Bound(work));
            Assert.Equal(1, lease.DisposalAttempts);
            Assert.False(lease.Content.CanRead);
            if (!failAnalyzer && !failAcknowledgment) Assert.Same(disposalFault, observed);
            else
            {
                var faults = Assert.IsType<AggregateException>(observed).Flatten().InnerExceptions;
                Assert.Equal(failAnalyzer && failAcknowledgment ? 3 : 2, faults.Count);
                Assert.Contains(disposalFault, faults);
                if (failAnalyzer) Assert.Contains(analyzerFault, faults);
                if (failAcknowledgment) Assert.Contains(acknowledgmentFault, faults);
            }
        }
        finally
        {
            ack.TrySetResult(false);
            reader.Lease?.ReleaseDisposal.TrySetResult(true);
            await Record.ExceptionAsync(() => ScanTestData.Bound(work));
        }
    }

    [Fact]
    public async Task CancellationDuringHashStillAcknowledgesFailureBeforeDisposal()
    {
        using var cancellation = new CancellationTokenSource();
        var ack = ScanTestData.Signal<bool>();
        var reader = new ScanTestReader();
        var analyzer = new ScanTestAnalyzer { Hash = async (_, token) => { await cancellation.CancelAsync(); token.ThrowIfCancellationRequested(); return new(1, ScanTestData.Digest); } };
        var sink = new ReviewSink { Failure = (_, _) => new(ack.Task) };
        var work = new ProtectedReviewWorker(reader, analyzer).RevalidateAsync(ReviewData.Request(), sink, cancellation.Token);
        await ScanTestData.Bound(sink.Submitted.Task);
        Assert.Equal(ReviewUnavailableReason.Canceled, Assert.Single(sink.Failures).Reason);
        Assert.Equal(1, reader.Live);
        ack.SetResult(false);
        await ScanTestData.Bound(work);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task EquivalentCanonicalPathAndUnknownAllocationKeepContentEvidence()
    {
        var reader = new ScanTestReader { Create = (_, open) => new ScanTestLease(open)
        { OpenedFacts = new(open.ExpectedPhysicalFileKey, 1, @"\\?\c:\PHOTOS\1.jpg", null, new(null, null, false)) } };
        var sink = new ReviewSink();
        await new ProtectedReviewWorker(reader, new ScanTestAnalyzer()).RevalidateAsync(ReviewData.Request(), sink, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(sink.Evidence).Final.Allocation.Stable);
        Assert.Empty(sink.Failures);
    }

    [Fact]
    public async Task StaleSessionDoesNotOpenNativeContent()
    {
        var request = ReviewData.Request();
        request = request with { Session = new ReviewSessionPin(Guid.NewGuid(), ScanTestData.Context().Pin) };
        var reader = new ScanTestReader();
        var sink = new ReviewSink();
        await new ProtectedReviewWorker(reader, new ScanTestAnalyzer()).RevalidateAsync(request, sink, TestContext.Current.CancellationToken);
        Assert.Equal(ReviewUnavailableReason.Stale, Assert.Single(sink.Failures).Reason);
        Assert.Empty(reader.Leases);
    }

    private static ProtectedFileFacts Alter(ProtectedFileFacts facts, string field) => field switch
    {
        "key" => facts with { PhysicalFileKey = new(9, new(9, 9)) },
        "length" => facts with { ByteLength = 2 },
        "path" => facts with { ResolvedPath = @"C:\Photos\reused.jpg" },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static Exception Failure(string kind) => kind switch
    {
        "missing" => new FileNotFoundException(),
        "directory" => new DirectoryNotFoundException(),
        "access" => new UnauthorizedAccessException(),
        "sharing" => new ScanReadException(new(ScanStageState.FailedRetryable, ScanReason.SharingViolation), "sharing"),
        "reparse" or "sync" or "placeholder" => new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation), "unsafe"),
        "unknown" => new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.UnknownSafety), "unknown"),
        "read" => new IOException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class DisposalFailureReader(Exception failure) : IProtectedFileReader
    {
        internal DisposalFailureLease? Lease { get; private set; }
        public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken)
        {
            Lease = new DisposalFailureLease(request, failure);
            return ValueTask.FromResult<IProtectedFileLease>(Lease);
        }
    }

    private sealed class DisposalFailureLease(ProtectedOpenRequest request, Exception failure) : IProtectedFileLease
    {
        private readonly ScanTestLease inner = new(request);
        internal TaskCompletionSource<bool> DisposalStarted { get; } = ScanTestData.Signal<bool>();
        internal TaskCompletionSource<bool> ReleaseDisposal { get; } = ScanTestData.Signal<bool>();
        internal int DisposalAttempts { get; private set; }
        public Stream Content => inner.Content;
        public ProtectedFileFacts OpenedFacts => inner.OpenedFacts;
        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken) => inner.RefreshAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            DisposalAttempts++;
            DisposalStarted.TrySetResult(true);
            await ReleaseDisposal.Task;
            await inner.DisposeAsync();
            throw failure;
        }
    }
}

internal static class ReviewData
{
    internal static ReviewRevalidationRequest Request(long id = 1, ScanRunContext? context = null)
    {
        var work = ScanTestData.Work(id, context);
        var member = new ScanFileOccurrence(work.Token, work.File.DiscoveredPath, work.File.ResolvedPath,
            work.File.PhysicalFileKey, ScanTestData.Success, ScanTestData.Success, ScanTestData.Revision(work), new(4096, 1, true));
        return new(new(Guid.NewGuid(), work.Context.Pin), work.Context, Guid.NewGuid(), 3, member, 2);
    }

    internal static ImmutableArray<CandidateGroup> Groups(ScanRunContext context, int count) =>
        [new(Guid.NewGuid(), 3, context.Pin, CandidateCategory.Exact,
            Enumerable.Range(1, count).Select(id => Request(id, context).Occurrence).ToImmutableArray(), [], false, null, new(null, RecoveryUnavailableReason.UnknownAllocation))];
}

internal sealed class ReviewSink : IReviewResultSink
{
    internal Func<ReviewRevalidationEvidence, ValueTask<bool>> Commit { get; init; } = _ => ValueTask.FromResult(true);
    internal Func<ReviewRevalidationRequest, ReviewUnavailableReason, ValueTask<bool>> Failure { get; init; } = (_, _) => ValueTask.FromResult(true);
    internal TaskCompletionSource<bool> Submitted { get; } = ScanTestData.Signal<bool>();
    internal ConcurrentQueue<ReviewRevalidationEvidence> Evidence { get; } = new();
    internal ConcurrentQueue<(ReviewRevalidationRequest Request, ReviewUnavailableReason Reason)> Failures { get; } = new();
    public ValueTask<bool> CommitRevalidationAsync(ReviewRevalidationEvidence evidence)
    {
        Evidence.Enqueue(evidence);
        var result = Commit(evidence);
        Submitted.TrySetResult(true);
        return result;
    }
    public ValueTask<bool> FailRevalidationAsync(ReviewRevalidationRequest request, ReviewUnavailableReason reason)
    {
        Failures.Enqueue((request, reason));
        var result = Failure(request, reason);
        Submitted.TrySetResult(true);
        return result;
    }
}
