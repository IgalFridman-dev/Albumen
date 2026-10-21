using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

// E4-S6: the photo model's matches add Maybe-similar sets; Look-alike sets stay hash-only.
public sealed class EmbeddingLookAlikeSetTests
{
    private const float Match = 0.95f;

    [Fact]
    public void AModelMatchMakesPhotosTheHashDoesNotRelateMaybeSimilar()
    {
        var (store, rows) = Commit([0, ulong.MaxValue]);

        Commit(store, (1, 2, Match));

        var set = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(CandidateCategory.Uncertain, set.Category);
        Assert.True(set.Uncertain);
        Assert.Null(set.KeeperOccurrenceId);
        Assert.Equal([rows[0].Token, rows[1].Token], set.Members.Select(member => member.Token));
        var edge = Assert.Single(set.Similarity);
        Assert.Equal(NearRelationshipKind.Uncertain, edge.Kind);
        Assert.Equal(Match, edge.Cosine);
        Assert.Equal(64, edge.HammingDistance);
    }

    [Fact]
    public void AMatchBelowTheThresholdRelatesNothing()
    {
        var (store, _) = Commit([0, ulong.MaxValue]);

        Commit(store, (1, 2, BitConverter.Int32BitsToSingle(
            BitConverter.SingleToInt32Bits(MaybeSimilarThresholds.CosineMinimum) - 1)));

        Assert.Empty(StoreFixture.Snapshot(store).Groups);
    }

    [Fact]
    public void AMatchAtTheThresholdRelatesThePair()
    {
        var (store, _) = Commit([0, ulong.MaxValue]);

        Commit(store, (1, 2, MaybeSimilarThresholds.CosineMinimum));

        Assert.Single(StoreFixture.Snapshot(store).Groups);
    }

    [Fact]
    public void APairTheHashRelatesKeepsTheHashEvidence()
    {
        // 1-2 are Near (1 bit), 3-4 are Uncertain (8 bits), 1-3 not related.
        var (store, _) = Commit([0, 0b1, 0xFFFF_FFFF_0000_0000, 0xFFFF_FFFF_0000_00FF]);
        var before = StoreFixture.Snapshot(store).Groups.Select(Shape).ToArray();

        Commit(store, (1, 2, 0.99f), (3, 4, 0.99f));

        var groups = StoreFixture.Snapshot(store).Groups;
        Assert.Equal(before, groups.Select(Shape));
        Assert.Equal(CandidateCategory.NearDuplicate, groups[0].Category);
        Assert.All(groups.SelectMany(group => group.Similarity), edge => Assert.Null(edge.Cosine));
    }

    [Fact]
    public void ALookAlikeSetThatTakesInAModelMatchStaysLookAlikeAndShowsAgainAsMaybeSimilar()
    {
        // 1-2 are Near; 3 is unrelated by the hash but the model matches it to both.
        var (store, rows) = Commit([0, 0b1, ulong.MaxValue]);
        var lookAlike = Shape(Assert.Single(StoreFixture.Snapshot(store).Groups));

        Commit(store, (1, 3, Match), (2, 3, 0.93f));

        var groups = StoreFixture.Snapshot(store).Groups;
        Assert.Equal(2, groups.Length);
        Assert.Equal(lookAlike, Shape(groups[0]));
        Assert.Equal(CandidateCategory.Uncertain, groups[1].Category);
        Assert.Equal(rows.Select(row => row.Token), groups[1].Members.Select(member => member.Token));
        Assert.Equal(3, groups[1].Similarity.Length);
        Assert.Equal(1, groups[1].Similarity.Count(edge => edge.Kind == NearRelationshipKind.Near));
        Assert.Equal(0.93f, groups[1].Similarity.Where(edge => edge.Cosine is not null).Min(edge => edge.Cosine));
    }

    [Fact]
    public void ModelMatchesDoNotChainPhotosTogether()
    {
        var (store, rows) = Commit([0, ulong.MaxValue, 0xFFFF_FFFF]);

        Commit(store, (1, 2, Match), (2, 3, Match));

        var groups = StoreFixture.Snapshot(store).Groups;
        Assert.Equal(2, groups.Length);
        Assert.Equal([rows[0].Token, rows[1].Token], groups[0].Members.Select(member => member.Token));
        Assert.Equal([rows[1].Token, rows[2].Token], groups[1].Members.Select(member => member.Token));
    }

    [Fact]
    public void AModelMatchBetweenTwoSetsIsNotShownAgainAsAPair()
    {
        // 1-2 and 3-4 are model sets; 1-3 matches too, but 1-4 does not, so the sets stay apart.
        var (store, _) = Commit(Spread(4));

        Commit(store, (1, 2, Match), (3, 4, Match), (1, 3, 0.93f));

        var groups = StoreFixture.Snapshot(store).Groups;
        Assert.Equal(2, groups.Length);
        Assert.Equal(["1,2", "3,4"], groups.Select(group =>
            string.Join(",", group.Members.Select(member => member.Token.OccurrenceId))));
    }

    [Fact]
    public void ModelSetsHaveAMaximumSize()
    {
        const int count = 40;
        var (store, _) = Commit(Spread(count));
        Assert.Empty(StoreFixture.Snapshot(store).Groups);

        Commit(store, [.. AllPairs(count, Match)]);

        var groups = StoreFixture.Snapshot(store).Groups;
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.InRange(group.Members.Length, 2, MaybeSimilarThresholds.MaximumSetSize));
        Assert.Equal(count, groups.Sum(group => group.Members.Length));
    }

    [Fact]
    public void SetsWithoutAModelMatchHaveNoMaximumSize()
    {
        // Every pair differs by 6 bits: Uncertain, as in 1.0.
        var count = MaybeSimilarThresholds.MaximumSetSize + 5;
        var hashes = Enumerable.Range(0, count).Select(index => Bits(index, 3) | Bits(count + index, 3)).ToArray();
        var (store, _) = Commit(hashes);

        Commit(store, (1, 2, Match));

        Assert.Equal(count, Assert.Single(StoreFixture.Snapshot(store).Groups).Members.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void LookAlikeSetsAreIdenticalWithAndWithoutTheModel(int seed)
    {
        var random = new Random(seed);
        var hashes = Families(random, 60);
        var (without, _) = Commit(hashes);
        var (with, _) = Commit(hashes);
        var matches = AllPairs(hashes.Length, 0f)
            .Where(_ => random.NextDouble() < 0.15)
            .Select(match => match with { Cosine = (float)(0.92 + random.NextDouble() * 0.08) })
            .ToArray();
        Assert.NotEmpty(matches);

        Commit(with, matches);

        var nearWithout = StoreFixture.Snapshot(without).Groups.Where(IsNear).Select(Shape).ToArray();
        var withGroups = StoreFixture.Snapshot(with).Groups;
        Assert.NotEmpty(nearWithout);
        Assert.Equal(nearWithout, withGroups.Where(IsNear).Select(Shape));
        Assert.Contains(withGroups, group => group.Similarity.Any(edge => edge.Cosine is not null));
        Assert.All(withGroups.Where(IsNear), group => Assert.Null(group.KeeperOccurrenceId));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void TheSameMatchesGiveTheSameSetsInTheSameOrder(int seed)
    {
        var random = new Random(seed);
        var hashes = Families(random, 50);
        var matches = AllPairs(hashes.Length, 0f)
            .Where(_ => random.NextDouble() < 0.2)
            .Select(match => match with { Cosine = (float)(0.92 + random.Next(4) * 0.02) })
            .ToArray();
        var (forward, _) = Commit(hashes);
        var (shuffled, _) = Commit(hashes);

        Commit(forward, matches);
        Commit(shuffled, [.. matches.OrderBy(_ => random.Next())]);

        Assert.Equal(StoreFixture.Snapshot(forward).Groups.Select(Shape),
            StoreFixture.Snapshot(shuffled).Groups.Select(Shape));
    }

    [Fact]
    public void ANewNearBatchDropsTheModelMatches()
    {
        var (store, rows) = Commit([0, ulong.MaxValue]);
        Commit(store, (1, 2, Match));

        Assert.True(store.CommitNearDuplicates(Batch(rows, [0, ulong.MaxValue])).Accepted);

        Assert.Empty(StoreFixture.Snapshot(store).Groups);
    }

    private static (ScanStore Store, ImmutableArray<ScanFileOccurrence> Rows) Commit(ulong[] hashes)
    {
        var store = new ScanStore(ScanTestData.Context());
        for (var id = 1; id <= hashes.Length; id++)
        {
            var work = StoreFixture.Register(store, id);
            var content = new ContentRevision(work.File.PhysicalFileKey!.Value, 100 + id,
                id.ToString("X64", CultureInfo.InvariantCulture));
            var facts = new ProtectedFileFacts(content.PhysicalFileKey, content.ByteLength,
                work.File.ResolvedPath, null, new AllocationEvidence(4096, 1, true));
            Assert.True(store.CommitSignature(new(work.Token, content, facts, facts)).Accepted);
        }
        var rows = StoreFixture.Snapshot(store).Occurrences;
        Assert.True(store.CommitNearDuplicates(Batch(rows, hashes)).Accepted);
        return (store, rows);
    }

    private static void Commit(ScanStore store, params (long Left, long Right, float Cosine)[] matches) =>
        Commit(store, matches.Select(match => new EmbeddingMatch(match.Left, match.Right, match.Cosine)).ToArray());

    private static void Commit(ScanStore store, EmbeddingMatch[] matches) =>
        Assert.True(store.Apply(store.PrepareEmbeddingMatches([.. matches])));

    private static NearDuplicateBatch Batch(ImmutableArray<ScanFileOccurrence> rows, ulong[] hashes) =>
        new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [.. rows.Select((row, index) => new NearFeatureResult(
            row.Token, row.Content, new PerceptualSignature("managed-dhash-bgra-v1", hashes[index], 100, 100, 100, 100),
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None)))]);

    private static IEnumerable<EmbeddingMatch> AllPairs(int count, float cosine) =>
        from left in Enumerable.Range(1, count)
        from right in Enumerable.Range(left + 1, count - left)
        select new EmbeddingMatch(left, right, cosine);

    private static ulong Bits(int block, int width) => ((1UL << width) - 1) << (block * width % 64);

    // Hashes at least 17 bits apart from each other, so the hash relates none of them.
    private static ulong[] Spread(int count)
    {
        var random = new Random(count);
        var hashes = new List<ulong>();
        while (hashes.Count < count)
        {
            var hash = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 40);
            if (hashes.All(other => BitOperations.PopCount(hash ^ other) > NearDuplicateThresholds.UncertainHammingMaximum))
                hashes.Add(hash);
        }
        return [.. hashes];
    }

    // Small families of Near and Uncertain photos around random centres, so Look-alike sets exist.
    private static ulong[] Families(Random random, int count)
    {
        var hashes = new List<ulong>();
        while (hashes.Count < count)
        {
            var centre = (ulong)random.NextInt64();
            var size = random.Next(1, 5);
            for (var index = 0; index < size && hashes.Count < count; index++)
            {
                var hash = centre;
                for (var flip = random.Next(0, 10); flip > 0; flip--) hash ^= 1UL << random.Next(64);
                hashes.Add(hash);
            }
        }
        return [.. hashes];
    }

    private static bool IsNear(CandidateGroup group) => group.Category == CandidateCategory.NearDuplicate;

    private static string Shape(CandidateGroup group) =>
        $"{group.Category}:{group.Uncertain}:{string.Join(",", group.Members.Select(row => row.Token.OccurrenceId))}:" +
        string.Join(";", group.Similarity.Select(edge =>
            $"{edge.Left.OccurrenceId}-{edge.Right.OccurrenceId}:{edge.Kind}:{edge.HammingDistance}:{edge.ColorDistance}:{edge.Cosine}"));
}
