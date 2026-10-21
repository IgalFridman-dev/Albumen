using System.Collections.Immutable;
using System.Globalization;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ScanEmbeddingMatchStageTests
{
    private static readonly SemanticSnapshot Ready = new(SemanticState.Ready, 3, 3, 3, "profile", "");

    [Fact]
    public async Task AfterTheIndexModelMatchesAddMaybeSimilarSetsAndKeepTheLookAlikeSet()
    {
        var matcher = new RecordingMatcher { Matches = [new(1, 3, 0.95f), new(2, 3, 0.95f)] };
        await using var fixture = Fixture(Ready, matcher);

        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        var (scanId, profile, contents) = Assert.Single(matcher.Calls);
        Assert.Equal(fixture.Owner.Current.Pin!.ScanId, scanId);
        Assert.Equal("profile", profile);
        Assert.Equal([1L, 2L, 3L], contents.Keys.Order());
        Assert.Equal(Digest(1), contents[1]);
        var groups = fixture.Owner.Current.Groups;
        Assert.Equal(2, groups.Length);
        Assert.Equal(CandidateCategory.NearDuplicate, groups[0].Category);
        Assert.Equal([1L, 2L], groups[0].Members.Select(member => member.Token.OccurrenceId));
        Assert.Equal(CandidateCategory.Uncertain, groups[1].Category);
        Assert.Equal([1L, 2L, 3L], groups[1].Members.Select(member => member.Token.OccurrenceId));
        Assert.Equal(Ready, fixture.Owner.Current.Semantic);
    }

    [Theory]
    [InlineData(SemanticState.Unavailable)]
    [InlineData(SemanticState.Failed)]
    [InlineData(SemanticState.Empty)]
    public async Task WithoutReadyVectorsReviewGetsTheHashSetsOnly(SemanticState state)
    {
        var matcher = new RecordingMatcher { Matches = [new(1, 3, 0.95f)] };
        await using var fixture = Fixture(new(state, 3, 3, 0, "profile", "reason"), matcher);

        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        Assert.Empty(matcher.Calls);
        Assert.Equal(CandidateCategory.NearDuplicate, Assert.Single(fixture.Owner.Current.Groups).Category);
    }

    [Fact]
    public async Task AMatcherFaultLeavesTheHashSetsAndTheScanCompletes()
    {
        var matcher = new RecordingMatcher { Match = _ => throw new InvalidOperationException("store") };
        await using var fixture = Fixture(Ready, matcher);

        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status is ScanStatus.Complete or ScanStatus.Faulted);

        Assert.Equal(ScanStatus.Complete, fixture.Owner.Current.Status);
        Assert.Equal(CandidateCategory.NearDuplicate, Assert.Single(fixture.Owner.Current.Groups).Category);
    }

    [Fact]
    public async Task CancelWhileMatchingSkipsOnlyTheModelSets()
    {
        var matcher = new RecordingMatcher
        {
            Match = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return [];
            },
        };
        await using var fixture = Fixture(Ready, matcher);
        await fixture.Start();
        await fixture.Pump(matcher.Started.Task);

        await fixture.Pump(fixture.Owner.CancelAsync());
        await fixture.Until(snapshot => snapshot.Status is ScanStatus.Complete or ScanStatus.Incomplete);

        Assert.Equal(ScanStatus.Complete, fixture.Owner.Current.Status);
        Assert.Equal(0, fixture.Owner.Current.Counts.Canceled);
        Assert.Equal(CandidateCategory.NearDuplicate, Assert.Single(fixture.Owner.Current.Groups).Category);
    }

    private static CoordinatorFixture Fixture(SemanticSnapshot indexed, IEmbeddingMatcher matcher)
    {
        var count = 0;
        var analyzer = new ScanTestAnalyzer
        {
            Hash = (_, _) => ValueTask.FromResult(new StreamDigest(1, Digest(Interlocked.Increment(ref count)))),
        };
        return new(new BoundedScanExecutor(new ScanTestEnumerator(3), new ScanTestReader(), analyzer),
            nearDuplicates: new SignatureNearPipeline([0, 0b1, ulong.MaxValue]),
            semanticIndex: new FixedIndexer(indexed), embeddingMatches: matcher);
    }

    private static string Digest(long id) => id.ToString("x64", CultureInfo.InvariantCulture);

    // Gives each photo the hash at its position: here 1-2 are Near and 3 is unrelated.
    private sealed class SignatureNearPipeline(ulong[] hashes) : INearDuplicatePipeline
    {
        public Task<NearDuplicateBatch> AnalyzeAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NearDuplicateBatch(context.Pin, BoundedNearDuplicatePipeline.ImplementationRevision,
                [.. rows.Select(row => new NearFeatureResult(row.Token, row.Content,
                    new PerceptualSignature(BoundedNearDuplicatePipeline.ImplementationRevision,
                        hashes[row.Token.OccurrenceId - 1], 100, 100, 100, 100),
                    new StageOutcome(ScanStageState.Succeeded, ScanReason.None)))]));
    }

    private sealed class FixedIndexer(SemanticSnapshot result) : ISemanticIndexer
    {
        public Task<SemanticSnapshot> IndexAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
            Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingMatcher : IEmbeddingMatcher
    {
        public List<(Guid ScanId, string Profile, IReadOnlyDictionary<long, string> Contents)> Calls { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ImmutableArray<EmbeddingMatch> Matches { get; init; } = [];
        public Func<CancellationToken, Task<ImmutableArray<EmbeddingMatch>>>? Match { get; init; }

        public async ValueTask<ImmutableArray<EmbeddingMatch>> MatchAsync(Guid scanId, string profile,
            IReadOnlyDictionary<long, string> contents, CancellationToken cancellationToken)
        {
            Calls.Add((scanId, profile, contents));
            Started.TrySetResult();
            return Match is null ? Matches : await Match(cancellationToken);
        }
    }
}
