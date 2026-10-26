using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class BoundedNearDuplicatePipelineTests
{
    private static readonly StageOutcome Succeeded = new(ScanStageState.Succeeded, ScanReason.None);

    [Fact]
    public async Task OneFeatureFailureIsIsolatedAndTheSingleBoundedWorkerReturnsEveryRow()
    {
        var (context, _, rows) = Scan(40);
        var source = new TrackingFeatureSource { Failing = 17 };
        var pipeline = new BoundedNearDuplicatePipeline(source, new FakeSignatureStore());

        var batch = await pipeline.AnalyzeAsync(context, rows,
            TestContext.Current.CancellationToken);

        Assert.Equal(BoundedNearDuplicatePipeline.Capacity, 16);
        Assert.Equal(1, source.MaximumActive);
        Assert.Equal(rows.Select(row => row.Token), batch.Results.Select(result => result.Token));
        Assert.Equal(ScanReason.ReadFailure, batch.Results[16].Outcome.Reason);
        Assert.Equal(39, batch.Results.Count(result => result.Outcome.State == ScanStageState.Succeeded));
    }

    [Fact]
    public async Task StoredFeaturesAreServedWithoutOpeningThePhoto()
    {
        var (context, store, rows) = Scan(3);
        var signatures = new FakeSignatureStore();
        foreach (var row in rows) Seed(signatures, row, near: Near(row), sharpness: 10.0 * row.Token.OccurrenceId);
        var source = new TrackingFeatureSource();

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        Assert.Empty(source.Calls);
        Assert.All(batch.Results, result =>
        {
            Assert.Equal(Succeeded, result.Outcome);
            Assert.Equal(Near(rows.Single(row => row.Token == result.Token)), result.Signature);
            Assert.Equal(10.0 * result.Token.OccurrenceId, result.Sharpness);
        });
        Assert.Equal(new SignatureReuse(new(3, 0, 0), new(3, 0, 0), SignatureReuseCounts.None), batch.Reuse);
        Assert.Equal([6], signatures.Lookups);
        Assert.Empty(signatures.Added);
        Assert.True(store.CommitNearDuplicates(batch).Accepted);
    }

    [Fact]
    public async Task MissedFeaturesAreComputedAndWrittenInBatchesOfSixtyFourPhotos()
    {
        var (context, store, rows) = Scan(70);
        var signatures = new FakeSignatureStore();
        var source = new TrackingFeatureSource();

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        Assert.Equal(70, source.Calls.Count);
        Assert.All(source.Calls, call => Assert.Equal(NearFeatures.All, call.Wanted));
        Assert.Equal([128, 12], signatures.Added.Select(added => added.Count));
        Assert.Equal(new SignatureReuse(new(0, 70, 0), new(0, 70, 0), SignatureReuseCounts.None), batch.Reuse);
        var first = rows[0];
        Assert.Equal(SignatureCodec.EncodeNear(Computed(first)), signatures.Rows[Key(first, SignatureKind.Near)]);
        Assert.Equal(SignatureCodec.EncodeSharpness(1.0), signatures.Rows[Key(first, SignatureKind.Sharpness)]);
        Assert.True(store.CommitNearDuplicates(batch).Accepted);
    }

    [Fact]
    public async Task AStoredNoScoreIsReusedLikeAScore()
    {
        var (context, _, rows) = Scan(1);
        var signatures = new FakeSignatureStore();
        Seed(signatures, rows[0], near: Near(rows[0]), sharpness: null);
        var source = new TrackingFeatureSource();

        var result = Assert.Single((await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken)).Results);

        Assert.Empty(source.Calls);
        Assert.Equal(Succeeded, result.Outcome);
        Assert.Null(result.Sharpness);
    }

    [Theory]
    [InlineData(SignatureKind.Sharpness)]
    [InlineData(SignatureKind.Near)]
    public async Task AFeatureStoredUnderAnotherRevisionIsTheOnlyOneComputed(SignatureKind changed)
    {
        var (context, store, rows) = Scan(1);
        var row = rows[0];
        var signatures = new FakeSignatureStore();
        Seed(signatures, row, near: Near(row), sharpness: 42.0);
        var stale = Key(row, changed);
        signatures.Rows[stale with { Revision = "older-revision" }] = signatures.Rows[stale];
        signatures.Rows.TryRemove(stale, out _);
        var source = new TrackingFeatureSource();

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        var result = Assert.Single(batch.Results);
        var written = Assert.Single(Assert.Single(signatures.Added));
        Assert.Equal(stale, written.Key);
        if (changed == SignatureKind.Sharpness)
        {
            Assert.Equal([(1L, NearFeatures.Sharpness)], source.Calls);
            Assert.Equal((Near(row), 1.0), (result.Signature, result.Sharpness));
            Assert.Equal(new SignatureReuse(new(1, 0, 0), new(0, 1, 0), SignatureReuseCounts.None), batch.Reuse);
        }
        else
        {
            Assert.Equal([(1L, NearFeatures.Signature)], source.Calls);
            Assert.Equal((Computed(row), 42.0), (result.Signature, result.Sharpness));
            Assert.Equal(new SignatureReuse(new(0, 1, 0), new(1, 0, 0), SignatureReuseCounts.None), batch.Reuse);
        }
        Assert.True(store.CommitNearDuplicates(batch).Accepted);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("add")]
    public async Task AStoreFailureIsARejectAndThePhotosAreComputedAsIfNothingWereStored(string failing)
    {
        var (context, store, rows) = Scan(3);
        var signatures = new FakeSignatureStore();
        if (failing == "lookup") signatures.LookupFailure = new IOException("locked");
        else signatures.AddFailure = new IOException("disk full");
        var source = new TrackingFeatureSource();

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        Assert.Equal(3, source.Calls.Count(call => call.Wanted == NearFeatures.All));
        Assert.All(batch.Results, result => Assert.Equal(Succeeded, result.Outcome));
        Assert.Equal(new SignatureReuse(new(0, 0, 3), new(0, 0, 3), SignatureReuseCounts.None), batch.Reuse);
        Assert.Equal(failing == "lookup" ? 0 : 1, signatures.Added.Count);
        Assert.True(store.CommitNearDuplicates(batch).Accepted);
    }

    [Fact]
    public async Task AStoredValueThatCannotBeTrueIsARejectAndIsComputed()
    {
        var (context, _, rows) = Scan(1);
        var signatures = new FakeSignatureStore();
        signatures.Rows[Key(rows[0], SignatureKind.Near)] = [1, 2, 3];
        signatures.Rows[Key(rows[0], SignatureKind.Sharpness)] = SignatureCodec.EncodeSharpness(double.NaN);
        var source = new TrackingFeatureSource();

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        Assert.Equal([(1L, NearFeatures.All)], source.Calls);
        Assert.Equal((Computed(rows[0]), 1.0), (batch.Results[0].Signature, batch.Results[0].Sharpness));
        Assert.Equal(new SignatureReuse(new(0, 0, 1), new(0, 0, 1), SignatureReuseCounts.None), batch.Reuse);
        Assert.Empty(signatures.Added);
    }

    [Fact]
    public async Task AFeatureThatFailedIsNeverWritten()
    {
        var (context, _, rows) = Scan(3);
        var signatures = new FakeSignatureStore();
        var source = new TrackingFeatureSource { Failing = 2 };

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        Assert.Equal(ScanReason.ReadFailure, batch.Results[1].Outcome.Reason);
        Assert.DoesNotContain(Assert.Single(signatures.Added), written => written.Key.Sha256 == Sha(2));
        Assert.Equal(4, signatures.Rows.Count);
        Assert.Equal(new(0, 3, 0), batch.Reuse.Near);
    }

    [Fact]
    public async Task APhotoWithoutProvenContentIsNeitherLookedUpNorCounted()
    {
        var (context, store, rows) = Scan(2);
        Assert.True(store.Fail(rows[1].Token, ScanTestData.ReadFailure));
        rows = StoreFixture.Snapshot(store).Occurrences;
        var signatures = new FakeSignatureStore();
        var source = new TrackingFeatureSource();

        var batch = await new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(
            context, rows, TestContext.Current.CancellationToken);

        Assert.Equal([2], signatures.Lookups);
        Assert.Equal([(1L, NearFeatures.All), (2L, NearFeatures.All)], source.Calls);
        Assert.Equal(ScanStageState.NotApplicable, batch.Results[1].Outcome.State);
        Assert.Equal(new(0, 1, 0), batch.Reuse.Near);
        Assert.True(store.CommitNearDuplicates(batch).Accepted);
    }

    [Fact]
    public async Task CancelKeepsTheBatchesAlreadyWritten()
    {
        var (context, _, rows) = Scan(100);
        var signatures = new FakeSignatureStore();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = new TrackingFeatureSource { CancelAfter = (70, cancel) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BoundedNearDuplicatePipeline(source, signatures).AnalyzeAsync(context, rows, cancel.Token));

        Assert.Equal([128], signatures.Added.Select(added => added.Count));
        Assert.Equal(128, signatures.Rows.Count);
    }

    private static (ScanRunContext Context, ScanStore Store, ImmutableArray<ScanFileOccurrence> Rows) Scan(int count)
    {
        var context = ScanTestData.Context();
        var store = new ScanStore(context);
        for (var id = 1; id <= count; id++)
        {
            var work = StoreFixture.Register(store, id);
            var evidence = StoreFixture.Signature(work);
            Assert.True(store.CommitSignature(evidence with
            {
                Content = new ContentRevision(work.File.PhysicalFileKey!.Value, 1, Sha(id)),
            }).Accepted);
        }
        return (context, store, StoreFixture.Snapshot(store).Occurrences);
    }

    private static string Sha(long id) => id.ToString("X64", CultureInfo.InvariantCulture);

    private static SignatureKey Key(ScanFileOccurrence row, SignatureKind kind) => new(row.Content!.Sha256,
        row.Content.ByteLength, kind, kind == SignatureKind.Near
            ? BoundedNearDuplicatePipeline.ImplementationRevision
            : BoundedNearDuplicatePipeline.SharpnessRevision);

    // What a stored row holds, and what the source computes: different hashes tell them apart.
    private static PerceptualSignature Near(ScanFileOccurrence row) => new(
        BoundedNearDuplicatePipeline.ImplementationRevision, 1000 + (ulong)row.Token.OccurrenceId, 1, 2, 3, 4);

    private static PerceptualSignature Computed(ScanFileOccurrence row) => new(
        BoundedNearDuplicatePipeline.ImplementationRevision, (ulong)row.Token.OccurrenceId, 100, 100, 100, 100);

    private static void Seed(FakeSignatureStore store, ScanFileOccurrence row, PerceptualSignature near, double? sharpness)
    {
        store.Rows[Key(row, SignatureKind.Near)] = SignatureCodec.EncodeNear(near);
        store.Rows[Key(row, SignatureKind.Sharpness)] = SignatureCodec.EncodeSharpness(sharpness);
    }

    // Computes the hash Computed(row) and the score 1.0 for what is wanted.
    private sealed class TrackingFeatureSource : INearFeatureSource
    {
        private int active;
        public long? Failing { get; init; }
        public (int Calls, CancellationTokenSource Source)? CancelAfter { get; init; }
        public ConcurrentQueue<(long OccurrenceId, NearFeatures Wanted)> Calls { get; } = new();
        public int MaximumActive { get; private set; }

        public async ValueTask<NearFeatureResult> AnalyzeAsync(
            ScanRunContext context, ScanFileOccurrence row, NearFeatures wanted, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref active);
            MaximumActive = Math.Max(MaximumActive, count);
            try
            {
                Calls.Enqueue((row.Token.OccurrenceId, wanted));
                if (CancelAfter is { } cancel && Calls.Count == cancel.Calls) await cancel.Source.CancelAsync();
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (row.Content is null)
                    return new(row.Token, null, null, new(ScanStageState.NotApplicable, row.Exact.Reason));
                if (row.Token.OccurrenceId == Failing) throw new IOException("injected");
                return new(row.Token, row.Content,
                    wanted.HasFlag(NearFeatures.Signature) ? Computed(row) : null, Succeeded,
                    wanted.HasFlag(NearFeatures.Sharpness) ? 1.0 : null);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
