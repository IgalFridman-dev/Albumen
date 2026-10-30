using System.Collections.Concurrent;
using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Semantic;

public sealed class BoundedSemanticIndexerTests
{
    [Fact]
    public async Task EveryPhotoWithProvenContentIsEmbeddedAndStoredUnderTheProfile()
    {
        var fixture = new IndexerFixture();
        var rows = fixture.Rows(3).Add(fixture.Row(4, exact: ScanTestData.ReadFailure));

        var result = await fixture.IndexAsync(rows);

        Assert.Equal(new SemanticSnapshot(SemanticState.Ready, 3, 3, 3, IndexerFixture.Profile.Fingerprint, "")
            { Vectors = new(0, 3, 0) }, result);
        Assert.False(result.IsPartial);
        Assert.Equal(["clear", "add"], fixture.Store.Calls.Distinct());
        Assert.Equal([1L, 2L, 3L], fixture.Store.Stored.Keys.Order());
        Assert.All(fixture.Store.Stored.Values, stored => Assert.Equal(IndexerFixture.Sha(stored.OccurrenceId), stored.Sha256));
        Assert.Equal(3, fixture.Progress[^1].Indexed);
        Assert.All(fixture.Progress, progress => Assert.Equal(SemanticState.Indexing, progress.State));
    }

    [Fact]
    public async Task UnreadableOrChangedPhotosAreSkippedAndDiscloseIncompleteCoverage()
    {
        var fixture = new IndexerFixture();
        fixture.Images.Unreadable.Add(2);
        fixture.Images.Throw[3] = new ScanReadException(ScanTestData.ReadFailure, "locked");

        var result = await fixture.IndexAsync(fixture.Rows(4));

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal((4, 4, 2), (result.Total, result.Processed, result.Indexed));
        Assert.True(result.IsPartial);
        Assert.Equal([1L, 4L], fixture.Store.Stored.Keys.Order());
    }

    [Fact]
    public async Task NoEligibleOrNoReadablePhotoIsEmpty()
    {
        var none = new IndexerFixture();
        Assert.Equal(SemanticState.Empty, (await none.IndexAsync([none.Row(1, exact: ScanTestData.Canceled)])).State);
        Assert.Equal(["clear"], none.Store.Calls);

        var unreadable = new IndexerFixture();
        unreadable.Images.Unreadable.Add(1);
        var result = await unreadable.IndexAsync(unreadable.Rows(1));
        Assert.Equal((SemanticState.Empty, 1, 0), (result.State, result.Total, result.Indexed));
    }

    [Theory]
    [InlineData(EncoderFailureKind.Unavailable, SemanticState.Unavailable)]
    [InlineData(EncoderFailureKind.Failed, SemanticState.Failed)]
    public async Task AModelThatCannotLoadTouchesNoPhotoAndNoStoredVector(EncoderFailureKind kind, SemanticState expected)
    {
        var fixture = new IndexerFixture();
        fixture.Encoder.LoadFailure = new(kind, "The model files are not installed.");

        var result = await fixture.IndexAsync(fixture.Rows(2));

        Assert.Equal((expected, 2, 0, "The model files are not installed."), (result.State, result.Total, result.Indexed, result.Reason));
        Assert.Empty(fixture.Store.Calls);
        Assert.Equal(0, fixture.Images.Reads);
        Assert.Empty(fixture.Signatures.Lookups);
    }

    [Fact]
    public async Task AnEncoderFailureStopsTheIndexAsFailedWithNoWorkerLeft()
    {
        var fixture = new IndexerFixture(workers: 2);
        fixture.Encoder.Failures[3] = new(EncoderFailureKind.Failed, "The model could not run.");

        var result = await fixture.IndexAsync(fixture.Rows(20));

        Assert.Equal((SemanticState.Failed, "The model could not run."), (result.State, result.Reason));
        Assert.Equal(0, fixture.Images.Live);
        Assert.Equal(0, fixture.Encoder.Live);
    }

    [Fact]
    public async Task AStoreFailureIsFailedAndNotThrown()
    {
        var fixture = new IndexerFixture();
        fixture.Store.AddFailure = new InvalidDataException("disk full");

        var result = await fixture.IndexAsync(fixture.Rows(2));

        Assert.Equal(SemanticState.Failed, result.State);
        Assert.Equal(0, fixture.Images.Live);
        Assert.Empty(fixture.Signatures.Added);
    }

    [Fact]
    public async Task AnUnexpectedPhotoReaderFaultSkipsOnlyThatPhoto()
    {
        var fixture = new IndexerFixture();
        fixture.Images.Throw[1] = new InvalidOperationException("bug");

        var result = await fixture.IndexAsync(fixture.Rows(2));

        Assert.Equal((SemanticState.Ready, 2, 2, 1), (result.State, result.Total, result.Processed, result.Indexed));
        Assert.Equal([2L], fixture.Store.Stored.Keys);
    }

    [Fact]
    public async Task CancelKeepsTheStoredVectorsAndReturnsThePartialIndex()
    {
        var fixture = new IndexerFixture(workers: 1);
        using var cancel = new CancellationTokenSource();
        // Photo 3 is still being encoded when Cancel arrives.
        fixture.Encoder.Gate = id => id == 3
            ? Task.Delay(Timeout.Infinite, cancel.Token).ContinueWith(_ => { }, TaskScheduler.Default)
            : Task.CompletedTask;
        fixture.OnProgress = progress =>
        {
            if (progress.Indexed == 2) cancel.Cancel();
        };

        var result = await fixture.IndexAsync(fixture.Rows(5), cancel);

        Assert.Equal((SemanticState.Ready, 5, 2), (result.State, result.Total, result.Indexed));
        Assert.True(result.IsPartial);
        Assert.Equal([1L, 2L], fixture.Store.Stored.Keys.Order());
        Assert.Equal(0, fixture.Images.Live);
        Assert.Equal([IndexerFixture.VectorKey(1), IndexerFixture.VectorKey(2)], fixture.Signatures.Rows.Keys.OrderBy(key => key.Sha256));
    }

    [Fact]
    public async Task CancelDuringTheSignatureWriteKeepsTheSavedVectorsCounted()
    {
        var fixture = new IndexerFixture();
        using var cancel = new CancellationTokenSource();
        // Cancel arrives while a batch's signatures are written, after its vectors were saved.
        fixture.Signatures.OnAdd = token =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
        };

        var result = await fixture.IndexAsync(fixture.Rows(3), cancel);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(3, fixture.Store.Stored.Count);
        Assert.Equal(fixture.Store.Stored.Count, result.Indexed);
    }

    [Fact]
    public async Task CancelBeforeAnyVectorIsEmpty()
    {
        var fixture = new IndexerFixture();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await fixture.IndexAsync(fixture.Rows(3), cancel);

        Assert.Equal((SemanticState.Empty, 0), (result.State, result.Indexed));
    }

    [Fact]
    public async Task WorkIsBoundedByTheWorkerCountAndTheBatchSize()
    {
        var fixture = new IndexerFixture(workers: 3);
        fixture.Encoder.Gate = _ => Task.Delay(1);

        var result = await fixture.IndexAsync(fixture.Rows(200));

        Assert.Equal((SemanticState.Ready, 200), (result.State, result.Indexed));
        Assert.InRange(fixture.Images.MaximumLive, 1, 3);
        Assert.InRange(fixture.Encoder.MaximumLive, 1, 3);
        Assert.All(fixture.Store.Batches, size => Assert.InRange(size, 1, BoundedSemanticIndexer.BatchSize));
    }

    [Fact]
    public async Task ProgressIsReportedPerSavedBatchNotPerPhoto()
    {
        var fixture = new IndexerFixture(workers: 4);

        var result = await fixture.IndexAsync(fixture.Rows(640));

        Assert.Equal((SemanticState.Ready, 640), (result.State, result.Indexed));
        // 640 photos are 10 full batches; a slow machine may add a few interval saves.
        Assert.InRange(fixture.Progress.Count, 2, 40);
        Assert.Equal(640, fixture.Progress[^1].Indexed);
    }

    // The b9e6761 walkthrough: the first search waited while the text model opened. Once the
    // Scan's photos are indexed, the indexer asks the encoder to open it, once and in the background,
    // under the profile the photos were embedded with (loaded once for both).
    [Fact]
    public async Task AReadyIndexOpensTheTextModelOnceAfterTheLastPhoto()
    {
        var fixture = new IndexerFixture();

        var result = await fixture.IndexAsync(fixture.Rows(3));
        var (profile, encodedBefore) = await fixture.Encoder.TextPrepared.Task
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(SemanticState.Ready, result.State);
        Assert.Equal(IndexerFixture.Profile, profile);
        Assert.Equal(3, encodedBefore);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Encoder.TextPreparations);
        Assert.Equal(1, fixture.Encoder.ProfileLoads);
    }

    // Opening the text model is only a head start for the search: its failure, returned or thrown,
    // leaves the index exactly as it is, and the search reports it when it opens the model itself.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATextModelThatCannotOpenLeavesTheReadyIndexAsItIs(bool throws)
    {
        var fixture = new IndexerFixture();
        if (throws) fixture.Encoder.TextFault = new InvalidOperationException("The text model broke.");
        else fixture.Encoder.TextFailure = new(EncoderFailureKind.Failed, "The tokenizer could not be loaded.");

        var result = await fixture.IndexAsync(fixture.Rows(2));
        await fixture.Encoder.TextPrepared.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(new SemanticSnapshot(SemanticState.Ready, 2, 2, 2, IndexerFixture.Profile.Fingerprint, "")
            { Vectors = new(0, 2, 0) }, result);
        Assert.All(fixture.Progress, progress => Assert.Equal(SemanticState.Indexing, progress.State));
    }

    // Without a Ready index there is nothing to search, so the text model stays closed.
    [Fact]
    public async Task AnIndexThatIsNotReadyLeavesTheTextModelClosed()
    {
        var empty = new IndexerFixture();
        var unavailable = new IndexerFixture();
        unavailable.Encoder.LoadFailure = new(EncoderFailureKind.Unavailable, "The model files are not installed.");
        var failed = new IndexerFixture();
        failed.Encoder.Failures[1] = new(EncoderFailureKind.Failed, "The model could not run.");

        Assert.Equal(SemanticState.Empty, (await empty.IndexAsync([empty.Row(1, exact: ScanTestData.Canceled)])).State);
        Assert.Equal(SemanticState.Unavailable, (await unavailable.IndexAsync(unavailable.Rows(2))).State);
        Assert.Equal(SemanticState.Failed, (await failed.IndexAsync(failed.Rows(2))).State);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.All(new[] { empty, unavailable, failed }, fixture => Assert.Equal(0, fixture.Encoder.TextPreparations));
    }

    [Fact]
    public async Task AStoredVectorIsCopiedIntoTheIndexWithoutReadingThePhoto()
    {
        var fixture = new IndexerFixture();
        fixture.Seed(1, 9f, 9f, 9f, 9f);
        fixture.Seed(2, 8f, 8f, 8f, 8f);
        var indexedAtWrite = 0;
        fixture.Signatures.OnAdd = _ => indexedAtWrite = fixture.Store.Stored.Count;

        var result = await fixture.IndexAsync(fixture.Rows(3));

        Assert.Equal((SemanticState.Ready, 3, 3), (result.State, result.Processed, result.Indexed));
        Assert.Equal(new(2, 1, 0), result.Vectors);
        Assert.Equal(1, fixture.Images.Reads);
        Assert.Equal([9f, 9f, 9f, 9f], fixture.Store.Stored[1].Vector);
        Assert.Equal([8f, 8f, 8f, 8f], fixture.Store.Stored[2].Vector);
        Assert.Equal([3f, 0f, 0f, 0f], fixture.Store.Stored[3].Vector);
        var written = Assert.Single(Assert.Single(fixture.Signatures.Added));
        Assert.Equal(IndexerFixture.VectorKey(3), written.Key);
        Assert.Equal(SignatureCodec.EncodeVector([3f, 0f, 0f, 0f]), written.Value);
        // The index holds the batch before its signatures are written.
        Assert.Equal(3, indexedAtWrite);
    }

    [Fact]
    public async Task AVectorMadeUnderAnotherProfileIsNotReused()
    {
        var fixture = new IndexerFixture();
        var other = IndexerFixture.Profile with { VisionSha256 = new string('E', 64) };
        fixture.Signatures.Rows[IndexerFixture.VectorKey(1) with { Revision = other.Fingerprint }] =
            SignatureCodec.EncodeVector([9f, 9f, 9f, 9f]);

        var result = await fixture.IndexAsync(fixture.Rows(1));

        Assert.Equal(new(0, 1, 0), result.Vectors);
        Assert.Equal(1, fixture.Images.Reads);
        Assert.Equal([1f, 0f, 0f, 0f], fixture.Store.Stored[1].Vector);
    }

    [Fact]
    public async Task VectorsAreLookedUpOneBatchAtATime()
    {
        var fixture = new IndexerFixture();

        await fixture.IndexAsync(fixture.Rows(130));

        Assert.Equal([64, 64, 2], fixture.Signatures.Lookups);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("add")]
    public async Task ASignatureStoreFailureIsARejectAndTheIndexIsStillReady(string failing)
    {
        var fixture = new IndexerFixture();
        if (failing == "lookup") fixture.Signatures.LookupFailure = new IOException("locked");
        else fixture.Signatures.AddFailure = new IOException("disk full");

        var result = await fixture.IndexAsync(fixture.Rows(3));

        Assert.Equal((SemanticState.Ready, 3), (result.State, result.Indexed));
        Assert.Equal(new(0, 0, 3), result.Vectors);
        Assert.Equal(3, fixture.Images.Reads);
        Assert.Equal(3, fixture.Store.Stored.Count);
    }

    [Fact]
    public async Task AStoredVectorOfTheWrongDimensionIsARejectAndIsEncoded()
    {
        var fixture = new IndexerFixture();
        fixture.Seed(1, 9f, 9f, 9f);

        var result = await fixture.IndexAsync(fixture.Rows(1));

        Assert.Equal(new(0, 0, 1), result.Vectors);
        Assert.Equal([1f, 0f, 0f, 0f], fixture.Store.Stored[1].Vector);
        Assert.Empty(fixture.Signatures.Added);
    }
}

internal sealed class IndexerFixture(int workers = 2)
{
    public static readonly EncoderProfile Profile = new(
        "siglip2-test", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 4, 1, 100, -10);

    private readonly ScanRunContext context = ScanTestData.Context();

    public FakeEncoder Encoder { get; } = new();
    public FakeImageSource Images { get; } = new();
    public FakeEmbeddingStore Store { get; } = new();
    public FakeSignatureStore Signatures { get; } = new();
    public List<SemanticSnapshot> Progress { get; } = [];
    public Action<SemanticSnapshot>? OnProgress { get; set; }

    public static string Sha(long id) => id.ToString("X64", System.Globalization.CultureInfo.InvariantCulture);

    public ScanFileOccurrence Row(long id, StageOutcome? exact = null)
    {
        var physical = new PhysicalFileKey(1, new FileId128(0, (ulong)id));
        var succeeded = exact is null;
        return new(new OccurrenceToken(context.Pin, id, 1, 1), $@"C:\Photos\{id}.jpg", $@"C:\Photos\{id}.jpg",
            physical, ScanTestData.Success, exact ?? ScanTestData.Success,
            succeeded ? new ContentRevision(physical, 10, Sha(id)) : null,
            new AllocationEvidence(10, 1, true));
    }

    public static SignatureKey VectorKey(long id) => new(Sha(id), 10, SignatureKind.Vector, Profile.Fingerprint);

    public void Seed(long id, params float[] vector) => Signatures.Rows[VectorKey(id)] = SignatureCodec.EncodeVector(vector);

    public ImmutableArray<ScanFileOccurrence> Rows(int count) =>
        [.. Enumerable.Range(1, count).Select(id => Row(id))];

    public Task<SemanticSnapshot> IndexAsync(
        ImmutableArray<ScanFileOccurrence> rows, CancellationTokenSource? cancel = null) =>
        ScanTestData.Bound(new BoundedSemanticIndexer(Encoder, Images, Store, Signatures, workers).IndexAsync(context, rows,
            progress =>
            {
                Progress.Add(progress);
                OnProgress?.Invoke(progress);
                return Task.CompletedTask;
            },
            cancel?.Token ?? TestContext.Current.CancellationToken));
}

internal sealed class FakeEncoder : IVisionLanguageEncoder
{
    private int live;
    private int maximumLive;
    private int encoded;
    private int textPreparations;
    private int profileLoads;

    public EncoderFailure? LoadFailure { get; set; }
    public int ProfileLoads => Volatile.Read(ref profileLoads);
    public ConcurrentDictionary<long, EncoderFailure> Failures { get; } = new();
    public Func<long, Task> Gate { get; set; } = _ => Task.CompletedTask;
    public int Live => Volatile.Read(ref live);
    public int MaximumLive => Volatile.Read(ref maximumLive);

    // The text model was asked to open: under which profile, and how many photos were encoded by then.
    public TaskCompletionSource<(EncoderProfile Profile, int EncodedBefore)> TextPrepared { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TextPreparations => Volatile.Read(ref textPreparations);
    public EncoderFailure? TextFailure { get; set; }
    public Exception? TextFault { get; set; }

    public EncoderProfileResult LoadProfile()
    {
        Interlocked.Increment(ref profileLoads);
        return LoadFailure is null ? new(IndexerFixture.Profile, null) : new(null, LoadFailure);
    }

    public EncoderFailure? PrepareText(EncoderProfile profile)
    {
        Interlocked.Increment(ref textPreparations);
        TextPrepared.TrySetResult((profile, Volatile.Read(ref encoded)));
        if (TextFault is not null) throw TextFault;
        return TextFailure;
    }

    public EmbeddingResult EncodeImage(EncoderProfile profile, EncoderImage image)
    {
        Assert.Equal(IndexerFixture.Profile, profile);
        var id = image.Width;
        var now = Interlocked.Increment(ref live);
        try
        {
            int seen;
            while (now > (seen = Volatile.Read(ref maximumLive))
                && Interlocked.CompareExchange(ref maximumLive, now, seen) != seen) { }
            Gate(id).Wait(TestContext.Current.CancellationToken);
            return Failures.TryGetValue(id, out var failure)
                ? new(default, failure)
                : new(new float[] { id, 0, 0, 0 }, null);
        }
        finally
        {
            Interlocked.Decrement(ref live);
            Interlocked.Increment(ref encoded);
        }
    }

    public EmbeddingResult EncodeText(EncoderProfile profile, string prompt) => throw new NotSupportedException();
}

internal sealed class FakeImageSource : IEncoderImageSource
{
    private int reads;
    private int live;
    private int maximumLive;

    public ConcurrentBag<long> Unreadable { get; } = [];
    public ConcurrentDictionary<long, Exception> Throw { get; } = new();
    public int Reads => Volatile.Read(ref reads);
    public int Live => Volatile.Read(ref live);
    public int MaximumLive => Volatile.Read(ref maximumLive);

    // The encoder fake reads the occurrence id back from the width.
    public async ValueTask<EncoderImage?> ReadAsync(
        ScanRunContext context, ScanFileOccurrence row, int maximumEdge, CancellationToken cancellationToken)
    {
        Assert.Equal(IndexerFixture.Profile.DecodeEdge, maximumEdge);
        Interlocked.Increment(ref reads);
        var now = Interlocked.Increment(ref live);
        int seen;
        while (now > (seen = Volatile.Read(ref maximumLive))
            && Interlocked.CompareExchange(ref maximumLive, now, seen) != seen) { }
        try
        {
            await Task.Yield();
            var id = row.Token.OccurrenceId;
            if (Throw.TryGetValue(id, out var exception)) throw exception;
            return Unreadable.Contains(id) ? null : new EncoderImage(new byte[(int)id * 4], (int)id, 1);
        }
        finally
        {
            Interlocked.Decrement(ref live);
        }
    }
}

internal sealed class FakeEmbeddingStore : IEmbeddingStore
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentDictionary<long, StoredEmbedding> Stored { get; } = new();
    public ConcurrentQueue<int> Batches { get; } = new();
    public Exception? AddFailure { get; set; }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        Calls.Enqueue("clear");
        Stored.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
        CancellationToken cancellationToken)
    {
        Calls.Enqueue("add");
        Assert.Equal(IndexerFixture.Profile, profile);
        if (AddFailure is not null) return ValueTask.FromException(AddFailure);
        Batches.Enqueue(embeddings.Count);
        foreach (var embedding in embeddings) Assert.True(Stored.TryAdd(embedding.OccurrenceId, embedding));
        return ValueTask.CompletedTask;
    }

    public ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
