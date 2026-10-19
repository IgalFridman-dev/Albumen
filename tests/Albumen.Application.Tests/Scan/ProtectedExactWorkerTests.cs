using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ProtectedExactWorkerTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SignatureLeaseLivesThroughAcknowledgmentEvenAfterCancellation(bool accepted, bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<SignatureCommit>();
        var reader = new ScanTestReader();
        var sink = new ScanTestSink { Signature = _ => { entered.SetResult(true); return new(acknowledge.Task); } };
        var work = new ProtectedExactWorker(reader, new ScanTestAnalyzer()).AnalyzeAsync(ScanTestData.Work(), sink, cancellation.Token);
        await ScanTestData.Bound(entered.Task);
        if (cancel) await cancellation.CancelAsync();
        Assert.False(work.IsCompleted);
        Assert.False(Assert.Single(reader.Leases).Disposed);
        acknowledge.SetResult(new SignatureCommit(accepted, []));
        await ScanTestData.Bound(work);
        Assert.True(Assert.Single(reader.Leases).Disposed);
        Assert.Empty(sink.Failures);
    }

    [Fact]
    public async Task SignatureCommitExceptionReleasesLeaseAndRemainsObservable()
    {
        var entered = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<SignatureCommit>();
        var reader = new ScanTestReader();
        var sink = new ScanTestSink { Signature = _ => { entered.SetResult(true); return new(acknowledge.Task); } };
        var work = new ProtectedExactWorker(reader, new ScanTestAnalyzer()).AnalyzeAsync(ScanTestData.Work(), sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(entered.Task);
        Assert.Equal(1, reader.Live);
        acknowledge.SetException(new InvalidOperationException("owner fault"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(work));
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAcknowledgmentRetainsSignatureLeaseForReadFailureAndCancellation(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var failed = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<bool>();
        var reader = new ScanTestReader();
        var analyzer = new ScanTestAnalyzer
        {
            Hash = async (_, token) =>
            {
                if (cancel) { await cancellation.CancelAsync(); token.ThrowIfCancellationRequested(); }
                throw new IOException("read failed");
            },
        };
        var sink = new ScanTestSink { Failure = (_, _) => { failed.SetResult(true); return new(acknowledge.Task); } };
        var work = new ProtectedExactWorker(reader, analyzer).AnalyzeAsync(ScanTestData.Work(), sink, cancellation.Token);
        await ScanTestData.Bound(failed.Task);
        Assert.Equal(1, reader.Live);
        Assert.False(work.IsCompleted);
        Assert.Equal(cancel ? ScanReason.Canceled : ScanReason.ReadFailure, Assert.Single(sink.Failures).Outcome.Reason);
        acknowledge.SetResult(false);
        await ScanTestData.Bound(work);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignatureClosesBeforeTwoComparisonLeasesWhichSurviveAcknowledgment(bool accepted)
    {
        var entered = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<bool>();
        var reader = new ScanTestReader();
        var occurrence = ScanTestData.Work();
        var comparison = ScanTestData.Comparison(occurrence);
        var sink = new ScanTestSink
        {
            Signature = _ => ValueTask.FromResult(new SignatureCommit(true, [comparison])),
            Comparison = evidence =>
            {
                Assert.Equal(comparison, evidence.Work);
                Assert.Equal(ScanTestData.Digest, evidence.Content.Left.Sha256);
                entered.SetResult(true);
                return new(acknowledge.Task);
            },
        };
        var work = new ProtectedExactWorker(reader, new ScanTestAnalyzer()).AnalyzeAsync(occurrence, sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(entered.Task);
        var leases = reader.Leases.ToArray();
        Assert.Equal(3, leases.Length);
        Assert.True(leases[0].Disposed);
        Assert.False(leases[1].Disposed);
        Assert.False(leases[2].Disposed);
        Assert.Equal(2, reader.MaximumLive);
        acknowledge.SetResult(accepted);
        await ScanTestData.Bound(work);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(2, "open")]
    [InlineData(3, "open")]
    [InlineData(2, "read")]
    [InlineData(3, "read")]
    [InlineData(2, "short")]
    [InlineData(3, "short")]
    [InlineData(2, "extra")]
    [InlineData(3, "extra")]
    [InlineData(2, "refresh")]
    [InlineData(3, "refresh")]
    public async Task ComparisonFailuresAffectOnlyTheFailingOccurrenceAndRetainOpenLeases(int failingOpen, string phase)
    {
        var reader = new ScanTestReader
        {
            Create = (number, request) =>
            {
                if (number == failingOpen && phase == "open") throw new ScanReadException(ScanTestData.ReadFailure, "open failed");
                var lease = new ScanTestLease(request);
                if (number == failingOpen && phase == "read") lease.Content = new FailingScanStream();
                if (number == failingOpen && phase == "short") lease.Content = new MemoryStream([]);
                if (number == failingOpen && phase == "extra") lease.Content = new MemoryStream([42, 42]);
                if (number == failingOpen && phase == "refresh")
                    lease.Refresh = _ => ValueTask.FromException<ProtectedFileFacts>(new ScanReadException(ScanTestData.ReadFailure, "refresh failed"));
                return lease;
            },
        };
        var entered = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<bool>();
        var occurrence = ScanTestData.Work();
        var comparisons = 0;
        var sink = new ScanTestSink
        {
            Signature = _ => ValueTask.FromResult(new SignatureCommit(true, [ScanTestData.Comparison(occurrence)])),
            Comparison = _ => { comparisons++; return ValueTask.FromResult(true); },
            Failure = (_, _) => { entered.SetResult(true); return new(acknowledge.Task); },
        };
        var work = new ProtectedExactWorker(reader, new ScanTestAnalyzer()).AnalyzeAsync(occurrence, sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(entered.Task);
        var failure = Assert.Single(sink.Failures);
        Assert.Equal(failingOpen == 2 ? 1 : 2, failure.Token.OccurrenceId);
        Assert.Equal(ScanReason.ReadFailure, failure.Outcome.Reason);
        Assert.Equal(phase == "open" ? failingOpen - 2 : 2, reader.Live);
        acknowledge.SetResult(false);
        await ScanTestData.Bound(work);
        Assert.Equal(0, reader.Live);
        Assert.Equal(0, comparisons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComparisonAcknowledgmentRetainsBothLeasesAfterCancellationOrOwnerException(bool ownerException)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var submitted = ScanTestData.Signal<bool>();
        var acknowledge = ScanTestData.Signal<bool>();
        var occurrence = ScanTestData.Work();
        var reader = new ScanTestReader();
        var sink = new ScanTestSink
        {
            Signature = _ => ValueTask.FromResult(new SignatureCommit(true, [ScanTestData.Comparison(occurrence)])),
            Comparison = _ => { submitted.SetResult(true); return new(acknowledge.Task); },
        };
        var work = new ProtectedExactWorker(reader, new ScanTestAnalyzer()).AnalyzeAsync(occurrence, sink, cancellation.Token);
        await ScanTestData.Bound(submitted.Task);
        await cancellation.CancelAsync();
        Assert.False(work.IsCompleted);
        Assert.Equal(2, reader.Live);
        if (ownerException)
        {
            acknowledge.SetException(new InvalidOperationException("comparison owner fault"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ScanTestData.Bound(work));
        }
        else
        {
            acknowledge.SetResult(false);
            await ScanTestData.Bound(work);
        }
        Assert.Equal(0, reader.Live);
        Assert.Empty(sink.Failures);
    }

    [Fact]
    public async Task ComparisonAllowsShortReadsAndEmptyBuffersWithoutInventingEof()
    {
        var occurrence = ScanTestData.Work();
        var comparison = ScanTestData.Comparison(occurrence);
        comparison = comparison with
        {
            LeftContent = new ContentRevision(comparison.LeftContent.PhysicalFileKey, 3, ScanTestData.Digest),
            RightContent = new ContentRevision(comparison.RightContent.PhysicalFileKey, 3, ScanTestData.Digest),
        };
        var reader = new ScanTestReader
        {
            Create = (_, request) =>
            {
                var lease = new ScanTestLease(request) { Content = new ShortReadScanStream() };
                lease.OpenedFacts = lease.OpenedFacts with { ByteLength = 3 };
                return lease;
            },
        };
        var analyzer = new ScanTestAnalyzer
        {
            Compare = async (left, right, token) =>
            {
                foreach (var stream in new[] { left, right })
                {
                    Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, token));
                    var bytes = new byte[3];
                    Assert.Equal(1, await stream.ReadAsync(bytes, token));
                    await stream.ReadExactlyAsync(bytes.AsMemory(1), token);
                    Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
                    Assert.Equal(0, await stream.ReadAsync(bytes, token));
                }
                var digest = new StreamDigest(3, ScanTestData.Digest);
                return new StreamEquality(true, digest, digest);
            },
        };
        var comparisons = 0;
        var sink = new ScanTestSink
        {
            Signature = _ => ValueTask.FromResult(new SignatureCommit(true, [comparison])),
            Comparison = _ => { comparisons++; return ValueTask.FromResult(true); },
        };
        await ScanTestData.Bound(new ProtectedExactWorker(reader, analyzer).AnalyzeAsync(occurrence, sink, TestContext.Current.CancellationToken));
        Assert.Equal(1, comparisons);
        Assert.Empty(sink.Failures);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task UnequalBytesAreCommittedAsNegativeEvidence()
    {
        var occurrence = ScanTestData.Work();
        ComparisonEvidence? observed = null;
        var analyzer = new ScanTestAnalyzer
        {
            Compare = (_, _, _) => ValueTask.FromResult(new StreamEquality(false,
                new StreamDigest(1, ScanTestData.Digest), new StreamDigest(1, ScanTestData.Digest))),
        };
        var sink = new ScanTestSink
        {
            Signature = _ => ValueTask.FromResult(new SignatureCommit(true, [ScanTestData.Comparison(occurrence)])),
            Comparison = evidence => { observed = evidence; return ValueTask.FromResult(true); },
        };
        await ScanTestData.Bound(new ProtectedExactWorker(new ScanTestReader(), analyzer)
            .AnalyzeAsync(occurrence, sink, TestContext.Current.CancellationToken));
        Assert.NotNull(observed);
        Assert.False(observed.Equal);
        Assert.Empty(sink.Failures);
    }

    [Fact]
    public async Task ReplacementRejectsPendingSignatureAndDuplicateDeliverySettlesWithoutOpeningComparisons()
    {
        var scheduler = new ManualScanScheduler();
        var inbox = new ScanOwnerInbox(scheduler, 1);
        var occurrence = ScanTestData.Work();
        var current = occurrence.Context.Pin;
        var accepted = new HashSet<OccurrenceToken>();
        var submitted = ScanTestData.Signal<bool>();
        var sink = new ScanTestSink
        {
            Signature = evidence =>
            {
                var ack = inbox.ResultAsync(() => new SignatureCommit(
                    evidence.Token.Pin == current && accepted.Add(evidence.Token), []));
                submitted.TrySetResult(true);
                return new(ack);
            },
        };
        var replace = inbox.CommandAsync(() => current = ScanTestData.Context().Pin);
        var reader = new ScanTestReader();
        var worker = new ProtectedExactWorker(reader, new ScanTestAnalyzer());
        var first = worker.AnalyzeAsync(occurrence, sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(submitted.Task);
        Assert.Equal(1, reader.Live);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(first));
        await replace;
        var duplicate = worker.AnalyzeAsync(occurrence, sink, TestContext.Current.CancellationToken);
        await ScanTestData.Bound(scheduler.PumpUntilAsync(duplicate));
        Assert.Empty(accepted);
        Assert.Equal(2, reader.Leases.Count);
        Assert.Equal(0, reader.Live);
    }
}
