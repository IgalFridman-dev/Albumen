using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Xunit;

namespace Albumen.Application.Tests.Semantic;

public sealed class StoredEmbeddingMatcherTests
{
    private static readonly Guid ScanId = Guid.NewGuid();

    [Fact]
    public void OnlyPairsAtOrAboveTheThresholdMatchInOccurrenceOrder()
    {
        var threshold = MaybeSimilarThresholds.CosineMinimum;
        var vectors = new[]
        {
            Embedding(3, Unit(0)),
            Embedding(1, Unit(0)),
            Embedding(2, Turned(threshold + 0.01f)),
            Embedding(4, Turned(threshold - 0.01f)),
            Embedding(5, Unit(1)),
        };

        var matches = StoredEmbeddingMatcher.Pairs(vectors, TestContext.Current.CancellationToken);

        Assert.Equal([(1L, 2L), (1L, 3L), (2L, 3L), (2L, 4L)], matches.Select(match => (match.Left, match.Right)));
        Assert.Equal(1f, matches[1].Cosine, 5);
        Assert.Equal(threshold + 0.01f, matches[0].Cosine, 5);
    }

    [Fact]
    public void TheCosineIsTheDotProductForEveryDimension()
    {
        var random = new Random(4);
        foreach (var dimension in new[] { 1, 7, 8, 9, 768 })
        {
            var left = Normalized(random, dimension);
            var right = Normalized(random, dimension);
            var expected = left.Zip(right, (a, b) => (double)a * b).Sum();

            Assert.Equal(expected, StoredEmbeddingMatcher.Cosine(left, right), 5);
            Assert.Equal(1f, StoredEmbeddingMatcher.Cosine(left, left), 5);
        }
    }

    [Fact]
    public async Task OnlyVectorsOfTheScannedContentUnderTheLoadedProfileTakePart()
    {
        var store = new ReadOnlyStore(Embedding(1, Unit(0)), Embedding(2, Unit(0)), Embedding(3, Unit(0)));
        var matcher = new StoredEmbeddingMatcher(new FakeEncoder(), store);
        var contents = new Dictionary<long, string> { [1] = Sha(1), [2] = Sha(2).ToUpperInvariant(), [3] = "changed" };

        var matches = await matcher.MatchAsync(ScanId, IndexerFixture.Profile.Fingerprint, contents,
            TestContext.Current.CancellationToken);

        Assert.Equal([(1L, 2L)], matches.Select(match => (match.Left, match.Right)));
        Assert.Equal((ScanId, IndexerFixture.Profile), store.Read);
    }

    [Fact]
    public async Task VectorsOfAnotherProfileOrWithNoModelMatchNothing()
    {
        var store = new ReadOnlyStore(Embedding(1, Unit(0)), Embedding(2, Unit(0)));
        var contents = new Dictionary<long, string> { [1] = Sha(1), [2] = Sha(2) };
        var token = TestContext.Current.CancellationToken;

        Assert.Empty(await new StoredEmbeddingMatcher(new FakeEncoder(), store)
            .MatchAsync(ScanId, "another profile", contents, token));
        Assert.Empty(await new StoredEmbeddingMatcher(
                new FakeEncoder { LoadFailure = new(EncoderFailureKind.Unavailable, "not installed") }, store)
            .MatchAsync(ScanId, IndexerFixture.Profile.Fingerprint, contents, token));
        Assert.Null(store.Read);
    }

    [Fact]
    public void CancelStopsTheComparison()
    {
        var vectors = Enumerable.Range(1, 50).Select(id => Embedding(id, Unit(0))).ToArray();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            StoredEmbeddingMatcher.Pairs(vectors, new CancellationToken(canceled: true)));
    }

    /// <summary>Records the brute-force time for a 10,000-photo Scan; asserts only that it completes.</summary>
    [Fact]
    [Trait("Category", "GoldenVm")]
    public void TenThousandPhotosAreComparedWithoutAVectorDatabase()
    {
        const int Photos = 10_000;
        var random = new Random(10);
        var vectors = Enumerable.Range(1, Photos).Select(id => Embedding(id, Normalized(random, 768))).ToArray();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var matches = StoredEmbeddingMatcher.Pairs(vectors, TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.Empty(matches);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"embedding-match: photos={Photos} cores={Environment.ProcessorCount} ms={clock.ElapsedMilliseconds}");
    }

    private static StoredEmbedding Embedding(long id, float[] vector) => new(id, Sha(id), vector);

    private static string Sha(long id) => $"{id:x64}";

    private static float[] Unit(int axis)
    {
        var vector = new float[16];
        vector[axis] = 1;
        return vector;
    }

    // A unit vector whose cosine with Unit(0) is the given value.
    private static float[] Turned(float cosine)
    {
        var vector = new float[16];
        vector[0] = cosine;
        vector[2] = MathF.Sqrt(1 - cosine * cosine);
        return vector;
    }

    private static float[] Normalized(Random random, int dimension)
    {
        var vector = Enumerable.Range(0, dimension).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var length = MathF.Sqrt(vector.Sum(value => value * value));
        return [.. vector.Select(value => value / length)];
    }

    private sealed class ReadOnlyStore(params StoredEmbedding[] stored) : IEmbeddingStore
    {
        public (Guid, EncoderProfile)? Read { get; private set; }

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
            CancellationToken cancellationToken)
        {
            Read = (scanId, profile);
            foreach (var embedding in stored) visit(embedding);
            return ValueTask.CompletedTask;
        }
    }
}
