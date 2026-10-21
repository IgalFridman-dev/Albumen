using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Semantic;

public sealed class LikelyMatchesQueryTests
{
    private static readonly EncoderProfile Profile = new(
        "siglip2-test", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 2, 1, 100, -10);

    private static readonly SemanticSnapshot Ready = new(SemanticState.Ready, 4, 4, 4, Profile.Fingerprint, "");

    [Fact]
    public async Task PhotosAreRankedByDescendingScoreWithTiesByOccurrenceId()
    {
        var fixture = new Fixture();
        fixture.Store.Add(4, Unit(0.11f));
        fixture.Store.Add(1, Unit(0.15f));
        fixture.Store.Add(3, Unit(0.11f));
        fixture.Store.Add(2, Unit(0.13f));

        var result = await fixture.RankAsync("at sea");

        Assert.Equal(LikelyMatchesOutcome.Matches, result.Outcome);
        Assert.Equal([1L, 2L, 3L, 4L], result.Matches.Select(match => match.OccurrenceId));
        // The score is the model's probability: sigmoid(100 * 0.11 - 10).
        Assert.Equal(0.73106f, result.Matches[2].Score, 5);
        Assert.Equal(result.Matches[2].Score, result.Matches[3].Score);
    }

    [Fact]
    public async Task PhotosBelowTheMinimumScoreAreLeftOutAndNoneLeftIsNoMatches()
    {
        var fixture = new Fixture();
        // With scale 100 and bias -10 the probability reaches MinimumScore (0.5) at cosine 0.1.
        fixture.Store.Add(1, Unit(0.1f));
        fixture.Store.Add(2, Unit(0.099f));
        fixture.Store.Add(3, Unit(-0.4f));

        var some = await fixture.RankAsync("at sea");
        Assert.Equal([1L], some.Matches.Select(match => match.OccurrenceId));

        var none = new Fixture();
        none.Store.Add(1, Unit(0.01f));
        var result = await none.RankAsync("at sea");
        Assert.Equal((LikelyMatchesOutcome.NoMatches, "at sea"), (result.Outcome, result.Prompt));
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task ARepeatedPromptOnlyEncodesTheTextAndReadsTheStoredVectorsOfThisScan()
    {
        var fixture = new Fixture();
        fixture.Store.Add(1, Unit(0.3f));
        fixture.Store.Add(2, Unit(0.8f));

        var first = await fixture.RankAsync("  at   sea ");
        var second = await fixture.RankAsync("at sea");

        Assert.Equal(first.Matches, second.Matches);
        Assert.Equal(["a photo of at sea", "a photo of at sea"], fixture.Encoder.Prompts);
        Assert.Equal(0, fixture.Encoder.Images);
        Assert.Equal([(fixture.ScanId, Profile), (fixture.ScanId, Profile)], fixture.Store.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t ")]
    public async Task AnEmptyPromptClearsWithoutEncoding(string prompt)
    {
        var fixture = new Fixture();

        var result = await fixture.RankAsync(prompt);

        Assert.Equal(LikelyMatchesOutcome.Cleared, result.Outcome);
        Assert.Empty(fixture.Encoder.Prompts);
        Assert.Empty(fixture.Store.Reads);
    }

    [Fact]
    public async Task APromptOverTheLimitIsRejectedNeverCut()
    {
        var fixture = new Fixture();
        var longest = new string('a', LikelyMatchesQuery.MaximumPromptLength);

        Assert.Equal(LikelyMatchesOutcome.NoMatches, (await fixture.RankAsync(longest)).Outcome);
        var rejected = await fixture.RankAsync(longest + "b");

        Assert.Equal((LikelyMatchesOutcome.TooLong, longest + "b"), (rejected.Outcome, rejected.Prompt));
        Assert.Equal([LikelyMatchesQuery.PromptTemplate + longest], fixture.Encoder.Prompts);
    }

    [Fact]
    public async Task APromptThatAlreadySaysPhotoIsEncodedAsWritten()
    {
        var fixture = new Fixture();

        await fixture.RankAsync("A Photo of my dog");

        Assert.Equal(["A Photo of my dog"], fixture.Encoder.Prompts);
    }

    [Theory]
    [InlineData(SemanticState.Idle)]
    [InlineData(SemanticState.Indexing)]
    [InlineData(SemanticState.Empty)]
    [InlineData(SemanticState.Unavailable)]
    [InlineData(SemanticState.Failed)]
    public async Task OnlyAReadyIndexIsSearched(SemanticState state)
    {
        var fixture = new Fixture();

        var result = await fixture.RankAsync("at sea", Ready with { State = state });

        Assert.Equal(LikelyMatchesOutcome.NotReady, result.Outcome);
        Assert.Empty(fixture.Encoder.Prompts);
        Assert.Empty(fixture.Store.Reads);
    }

    [Fact]
    public async Task VectorsOfAnotherProfileAreNeverRead()
    {
        var fixture = new Fixture();

        var result = await fixture.RankAsync("at sea", Ready with { Profile = new string('0', 64) });

        Assert.Equal(LikelyMatchesOutcome.Failed, result.Outcome);
        Assert.Equal("The search model changed after this Scan. Scan again to search.", result.Reason);
        Assert.Empty(fixture.Store.Reads);
    }

    [Theory]
    [InlineData(EncoderFailureKind.Unavailable, LikelyMatchesOutcome.Unavailable)]
    [InlineData(EncoderFailureKind.Failed, LikelyMatchesOutcome.Failed)]
    public async Task EncoderFailuresAreReturnedNotThrown(EncoderFailureKind kind, LikelyMatchesOutcome expected)
    {
        var load = new Fixture();
        load.Encoder.LoadFailure = new(kind, "The model files are not installed.");
        var loaded = await load.RankAsync("at sea");
        Assert.Equal((expected, "The model files are not installed."), (loaded.Outcome, loaded.Reason));

        var text = new Fixture();
        text.Encoder.TextFailure = new(kind, "The model could not run.");
        var encoded = await text.RankAsync("at sea");
        Assert.Equal((expected, "The model could not run."), (encoded.Outcome, encoded.Reason));
        Assert.Empty(text.Store.Reads);
    }

    [Fact]
    public async Task AVectorWhoseContentIsNotThePhotosContentInTheScanIsNotUsed()
    {
        var fixture = new Fixture();
        fixture.Store.Add(1, Unit(0.9f));
        fixture.Store.Add(2, Unit(0.8f), sha256: IndexerFixture.Sha(99));
        fixture.Store.Add(700, Unit(0.7f));

        var result = await fixture.RankAsync("at sea");

        Assert.Equal([1L], result.Matches.Select(match => match.OccurrenceId));
    }

    [Fact]
    public async Task AStoreFaultIsFailed()
    {
        var fixture = new Fixture();
        fixture.Store.ReadFailure = new InvalidDataException("A stored vector does not have the profile's dimension.");

        var result = await fixture.RankAsync("at sea");

        Assert.Equal((LikelyMatchesOutcome.Failed, "The photo search data could not be read."), (result.Outcome, result.Reason));
    }

    [Fact]
    public async Task TheOrderIsTheSameWhateverOrderTheVectorsAreReadIn()
    {
        var random = new Random(1234);
        var vectors = Enumerable.Range(1, 500)
            .Select(id => (Id: (long)id, Vector: Unit((float)Math.Round(random.NextDouble() * 0.1 + 0.08, 3))))
            .ToArray();
        var ordered = new Fixture();
        foreach (var (id, vector) in vectors) ordered.Store.Add(id, vector);
        var shuffled = new Fixture();
        foreach (var (id, vector) in vectors.OrderBy(_ => random.Next())) shuffled.Store.Add(id, vector);

        var expected = (await ordered.RankAsync("at sea")).Matches;
        var actual = (await shuffled.RankAsync("at sea")).Matches;

        Assert.Equal(expected, actual);
        Assert.Contains(expected.Zip(expected.Skip(1)), pair => pair.First.Score == pair.Second.Score);
        Assert.All(expected.Zip(expected.Skip(1)), pair => Assert.True(
            pair.First.Score > pair.Second.Score
            || pair.First.Score == pair.Second.Score && pair.First.OccurrenceId < pair.Second.OccurrenceId));
    }

    [Fact]
    public void NormalizingCollapsesWhitespace() =>
        Assert.Equal("kids at sea", LikelyMatchesQuery.Normalize("\tkids \n at    sea  "));

    // The prompt encodes as (1, 0), so a photo's score is the first component of its vector.
    private static float[] Unit(float score) => [score, MathF.Sqrt(1 - score * score)];

    // A Scan of photos 1 to 500 whose content is IndexerFixture.Sha(id).
    private sealed class Fixture
    {
        private readonly ScanSnapshot scan;

        public Fixture()
        {
            var pin = new ScanPin(Guid.NewGuid(), 1, 1, 1);
            var occurrences = Enumerable.Range(1, 500).Select(id => Row(pin, id)).ToImmutableArray();
            scan = new ScanSnapshot(pin, ScanStatus.Complete, ScanTestData.Success,
                occurrences, [], new(500, 500, 0, 0, 0, 0)) { Semantic = Ready };
        }

        public TextEncoder Encoder { get; } = new();
        public VectorStore Store { get; } = new();
        public Guid ScanId => scan.Pin!.ScanId;

        public Task<LikelyMatchesResult> RankAsync(string prompt, SemanticSnapshot? index = null) =>
            new LikelyMatchesQuery(Encoder, Store).RankAsync(scan with { Semantic = index ?? Ready }, prompt,
                TestContext.Current.CancellationToken);

        private static ScanFileOccurrence Row(ScanPin pin, long id)
        {
            var physical = new PhysicalFileKey(1, new FileId128(0, (ulong)id));
            return new(new OccurrenceToken(pin, id, 1, 1), $@"C:\Photos\{id}.jpg", $@"C:\Photos\{id}.jpg",
                physical, ScanTestData.Success, ScanTestData.Success, new ContentRevision(physical, 10, IndexerFixture.Sha(id)),
                new AllocationEvidence(10, 1, true));
        }
    }

    private sealed class TextEncoder : IVisionLanguageEncoder
    {
        public EncoderFailure? LoadFailure { get; set; }
        public EncoderFailure? TextFailure { get; set; }
        public List<string> Prompts { get; } = [];
        public int Images { get; private set; }

        public EncoderProfileResult LoadProfile() => LoadFailure is null ? new(Profile, null) : new(null, LoadFailure);

        public EmbeddingResult EncodeImage(EncoderProfile profile, EncoderImage image)
        {
            Images++;
            throw new NotSupportedException();
        }

        public EmbeddingResult EncodeText(EncoderProfile profile, string prompt)
        {
            Assert.Equal(Profile, profile);
            lock (Prompts) Prompts.Add(prompt);
            return TextFailure is null ? new(new float[] { 1, 0 }, null) : new(default, TextFailure);
        }
    }

    private sealed class VectorStore : IEmbeddingStore
    {
        private readonly List<StoredEmbedding> stored = [];

        public List<(Guid, EncoderProfile)> Reads { get; } = [];
        public Exception? ReadFailure { get; set; }

        public void Add(long id, float[] vector, string? sha256 = null) =>
            stored.Add(new(id, sha256 ?? IndexerFixture.Sha(id), vector));

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
            CancellationToken cancellationToken)
        {
            Reads.Add((scanId, profile));
            if (ReadFailure is not null) return ValueTask.FromException(ReadFailure);
            foreach (var embedding in stored) visit(embedding);
            return ValueTask.CompletedTask;
        }
    }
}
