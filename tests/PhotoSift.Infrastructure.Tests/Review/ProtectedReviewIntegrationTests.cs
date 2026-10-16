using System.Security.Cryptography;
using System.Text;
using PhotoSift.Analysis.Exact;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Tests.Scan;
using PhotoSift.Infrastructure.Windows.Scan;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Review;

public sealed class ProtectedReviewIntegrationTests
{
    [Theory]
    [InlineData("accepted")]
    [InlineData("stale")]
    [InlineData("faulted")]
    public async Task NativeProtectionPersistsUntilReviewAcknowledgmentSettles(string outcome)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("review.jpg");
        var request = Request(fixture.Request(path));
        var ack = Signal<bool>();
        var sink = new NativeReviewSink { Commit = _ => new(ack.Task) };
        using var cancellation = new CancellationTokenSource();
        var work = new ProtectedReviewWorker(new WindowsProtectedFileReader(), new StreamingExactContentAnalyzer())
            .RevalidateAsync(request, sink, cancellation.Token);
        try
        {
            await Bound(sink.Submitted.Task);
            Assert.NotNull(sink.Evidence);
            await cancellation.CancelAsync();
            Assert.False(work.IsCompleted);
            Assert.Equal(32, fixture.TryDelete(path));
            Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            if (outcome == "faulted")
            {
                ack.SetException(new InvalidOperationException("owner bug"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Bound(work));
            }
            else { ack.SetResult(outcome == "accepted"); await Bound(work); }
            using var after = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        finally { ack.TrySetResult(false); try { await Bound(work); } catch (InvalidOperationException) when (outcome == "faulted") { } }
    }

    [Theory]
    [InlineData("missing", ReviewUnavailableReason.Missing)]
    [InlineData("reused", ReviewUnavailableReason.Changed)]
    [InlineData("content", ReviewUnavailableReason.Changed)]
    [InlineData("length", ReviewUnavailableReason.Changed)]
    [InlineData("root", ReviewUnavailableReason.Changed)]
    [InlineData("top-level", ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("reparse", ReviewUnavailableReason.UnsafeLocation)]
    public async Task CurrentNativeObjectMustMatchOriginalScopeIdentityAndContent(string change, ReviewUnavailableReason expected)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(change == "top-level" ? Path.Combine("album", "review.jpg") : "root.jpg");
        var request = Request(fixture.Request(path, change == "top-level" ? ScopeRecursion.TopLevelOnly : ScopeRecursion.IncludeSubfolders));
        switch (change)
        {
            case "missing": File.Delete(path); break;
            case "reused": File.Move(path, fixture.PathFor("old.jpg")); fixture.CreateFile("root.jpg"); break;
            case "content": File.WriteAllText(path, "changed"); break;
            case "length": File.WriteAllText(path, "longer fixture"); break;
            case "root": fixture.ReplaceRoot(); break;
            case "reparse":
                File.Move(path, fixture.PathFor("target.jpg"));
                File.CreateSymbolicLink(path, fixture.PathFor("target.jpg"));
                break;
        }
        var sink = new NativeReviewSink();
        await new ProtectedReviewWorker(new WindowsProtectedFileReader(), new StreamingExactContentAnalyzer())
            .RevalidateAsync(request, sink, TestContext.Current.CancellationToken);
        Assert.Null(sink.Evidence);
        Assert.Same(request, sink.FailureRequest);
        Assert.Equal(expected, sink.Reason);
    }

    [Theory]
    [InlineData("sync", false, ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("placeholder", false, ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("unknown", false, ReviewUnavailableReason.UnknownSafety)]
    [InlineData("escape", false, ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("sync", true, ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("placeholder", true, ReviewUnavailableReason.UnsafeLocation)]
    [InlineData("unknown", true, ReviewUnavailableReason.UnknownSafety)]
    [InlineData("identity", true, ReviewUnavailableReason.Changed)]
    public async Task NativeAncestorSafetyIsReestablishedAtOpenAndFinalRefresh(string change, bool final, ReviewUnavailableReason expected)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("album", "review.jpg"));
        var ancestor = Path.GetDirectoryName(path)!;
        var changed = !final;
        var source = new AlteredFileFacts((candidate, facts) => changed && candidate == ancestor ? change switch
        {
            "sync" => facts with { CloudState = ScopeCloudState.Synchronized },
            "placeholder" => facts with { CloudState = ScopeCloudState.Placeholder },
            "unknown" => facts with { CloudState = ScopeCloudState.Unknown },
            "identity" => facts with { PhysicalFileKey = new(5, new(6, 7)) },
            "escape" => facts with { ResolvedPath = fixture.ContainerPath },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        } : facts);
        var ack = Signal<bool>();
        var sink = new NativeReviewSink { Failure = (_, _) => new(ack.Task) };
        var analyzer = new AfterHashAnalyzer(() => changed = true);
        var work = new ProtectedReviewWorker(new WindowsProtectedFileReader(source), analyzer)
            .RevalidateAsync(Request(fixture.Request(path)), sink, TestContext.Current.CancellationToken);
        try
        {
            await Bound(sink.Submitted.Task);
            Assert.Null(sink.Evidence);
            Assert.Equal(expected, sink.Reason);
            if (final) Assert.Equal(32, fixture.TryDelete(path));
            ack.SetResult(false);
            await Bound(work);
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        finally { ack.TrySetResult(false); await Bound(work); }
    }

    [Fact]
    public async Task RetargetedContainedDirectoryAliasFailsFinalReviewRefresh()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("actual", "source.jpg"));
        fixture.CreateFile(Path.Combine("other", "source.jpg"));
        var alias = fixture.PathFor("alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(path)!);
        try
        {
            var open = fixture.Request(path) with { DiscoveredPath = Path.Combine(alias, "source.jpg") };
            var analyzer = new AfterHashAnalyzer(() => { Directory.Delete(alias); Directory.CreateSymbolicLink(alias, fixture.PathFor("other")); });
            var sink = new NativeReviewSink();
            await new ProtectedReviewWorker(new WindowsProtectedFileReader(), analyzer)
                .RevalidateAsync(Request(open), sink, TestContext.Current.CancellationToken);
            Assert.Null(sink.Evidence);
            Assert.Equal(ReviewUnavailableReason.Changed, sink.Reason);
        }
        finally { Directory.Delete(alias); }
    }

    private static ReviewRevalidationRequest Request(ProtectedOpenRequest open)
    {
        var success = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var member = new ScanFileOccurrence(new(open.Context.Pin, 1, 3, 1), open.DiscoveredPath, open.ResolvedPath,
            open.ExpectedPhysicalFileKey, success, success, new(open.ExpectedPhysicalFileKey, 7,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture")))), new(null, null, false));
        return new(new(Guid.NewGuid(), open.Context.Pin), open.Context, Guid.NewGuid(), 2, member, 1);
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private sealed class NativeReviewSink : IReviewResultSink
    {
        internal Func<ReviewRevalidationEvidence, ValueTask<bool>> Commit { get; init; } = _ => ValueTask.FromResult(true);
        internal Func<ReviewRevalidationRequest, ReviewUnavailableReason, ValueTask<bool>> Failure { get; init; } = (_, _) => ValueTask.FromResult(true);
        internal TaskCompletionSource<bool> Submitted { get; } = Signal<bool>();
        internal ReviewRevalidationEvidence? Evidence { get; private set; }
        internal ReviewRevalidationRequest? FailureRequest { get; private set; }
        internal ReviewUnavailableReason Reason { get; private set; }
        public ValueTask<bool> CommitRevalidationAsync(ReviewRevalidationEvidence evidence)
        { Evidence = evidence; var result = Commit(evidence); Submitted.TrySetResult(true); return result; }
        public ValueTask<bool> FailRevalidationAsync(ReviewRevalidationRequest request, ReviewUnavailableReason reason)
        { FailureRequest = request; Reason = reason; var result = Failure(request, reason); Submitted.TrySetResult(true); return result; }
    }

    private sealed class AfterHashAnalyzer(Action afterHash) : IExactContentAnalyzer
    {
        private readonly StreamingExactContentAnalyzer analyzer = new();
        public async ValueTask<StreamDigest> HashAsync(Stream content, long expectedLength, CancellationToken cancellationToken)
        { var result = await analyzer.HashAsync(content, expectedLength, cancellationToken); afterHash(); return result; }
        public ValueTask<StreamEquality> CompareAsync(Stream left, Stream right, long expectedLength, CancellationToken cancellationToken) =>
            analyzer.CompareAsync(left, right, expectedLength, cancellationToken);
    }
}
