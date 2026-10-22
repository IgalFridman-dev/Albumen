using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class NearDuplicateGroupTests
{
    [Fact]
    public void ByteIdenticalOccurrencesRemainExactAndAreExcludedFromNearGroups()
    {
        var store = new ScanStore(ScanTestData.Context());
        var first = StoreFixture.Register(store, 1);
        Assert.True(store.CommitSignature(StoreFixture.Signature(first)).Accepted);
        var second = StoreFixture.Register(store, 2);
        var comparison = Assert.Single(store.CommitSignature(StoreFixture.Signature(second)).Comparisons);
        Assert.True(store.CommitComparison(StoreFixture.Equality(comparison)));
        var rows = StoreFixture.Snapshot(store).Occurrences;

        var committed = store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1",
            [Success(rows[0], 0), Success(rows[1], 1)]));
        var snapshot = StoreFixture.Snapshot(store);

        Assert.True(committed.Accepted);
        var exact = Assert.Single(snapshot.Groups);
        Assert.Equal(CandidateCategory.Exact, exact.Category);
        Assert.Equal([rows[0].Token, rows[1].Token], exact.Members.Select(member => member.Token));
        Assert.DoesNotContain(snapshot.Groups, group => group.Category != CandidateCategory.Exact);
    }

    [Fact]
    public void AtomicCommitKeepsExactDistinctAndPublishesDeterministicLookAlikeSets()
    {
        var store = ExactRows(4);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var batch = new NearDuplicateBatch(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0b0000), Success(rows[1], 0b0001),
            Success(rows[2], 0b1111), Success(rows[3], 0b1111111111111111)]);

        var first = store.CommitNearDuplicates(batch);
        var firstSnapshot = StoreFixture.Snapshot(store);
        var second = store.CommitNearDuplicates(batch);
        var secondSnapshot = StoreFixture.Snapshot(store);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        // Rows 0-2 are Near to each other and row 3 is Uncertain to all three, so they form
        // one Uncertain set that keeps every pair's evidence instead of a Near group plus pairs.
        var set = Assert.Single(firstSnapshot.Groups);
        Assert.Equal(CandidateCategory.Uncertain, set.Category);
        Assert.True(set.Uncertain);
        Assert.Equal(rows.Select(row => row.Token), set.Members.Select(member => member.Token));
        Assert.Equal(6, set.Similarity.Length);
        Assert.Equal(3, set.Similarity.Count(edge => edge.Kind == NearRelationshipKind.Near));
        Assert.Equal(firstSnapshot.Groups.Select(GroupShape), secondSnapshot.Groups.Select(GroupShape));
        Assert.All(firstSnapshot.Groups.Where(group => group.Category != CandidateCategory.Exact),
            group => Assert.Null(group.KeeperOccurrenceId));
    }

    [Fact]
    public void NearComponentWithNothingElseRelatedStaysNear()
    {
        var store = ExactRows(3);
        var rows = StoreFixture.Snapshot(store).Occurrences;

        Assert.True(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0), Success(rows[1], 0b1), Success(rows[2], ulong.MaxValue)])).Accepted);

        var group = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(CandidateCategory.NearDuplicate, group.Category);
        Assert.False(group.Uncertain);
        Assert.Equal([rows[0].Token, rows[1].Token], group.Members.Select(member => member.Token));
        Assert.All(group.Similarity, edge => Assert.Equal(NearRelationshipKind.Near, edge.Kind));
    }

    [Fact]
    public void SixMutuallySimilarPhotosFormOneSetInsteadOfPairs()
    {
        var store = ExactRows(6);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        // Each photo sets its own 3-bit block, so every pair differs by 6 bits: Uncertain.
        var results = rows.Select((row, index) => Success(row, 0b111UL << (3 * index))).ToImmutableArray();

        Assert.True(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", results)).Accepted);

        var set = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(CandidateCategory.Uncertain, set.Category);
        Assert.Equal(rows.Select(row => row.Token), set.Members.Select(member => member.Token));
        Assert.Equal(15, set.Similarity.Length);
        Assert.Null(set.KeeperOccurrenceId);
    }

    [Fact]
    public void LooselyChainedPhotosAreNotForcedIntoOneSet()
    {
        var store = ExactRows(3);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        // A-B and B-C differ by 12 bits (Uncertain); A-C differs by 24 bits (not related).
        Assert.True(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0), Success(rows[1], 0xFFF), Success(rows[2], 0xFFFFFF)])).Accepted);

        var groups = StoreFixture.Snapshot(store).Groups;

        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Equal(CandidateCategory.Uncertain, group.Category));
        Assert.Equal([rows[0].Token, rows[1].Token], groups[0].Members.Select(member => member.Token));
        Assert.Equal([rows[1].Token, rows[2].Token], groups[1].Members.Select(member => member.Token));
    }

    [Fact]
    public void TwoNearComponentsThatAreRelatedEverywhereMergeIntoOneSet()
    {
        var store = ExactRows(4);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        // Rows 0-1 and rows 2-3 are Near pairs; every cross pair differs by 8 or 9 bits.
        Assert.True(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0), Success(rows[1], 0b1),
            Success(rows[2], 0xFF00), Success(rows[3], 0xFF01)])).Accepted);

        var set = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(CandidateCategory.Uncertain, set.Category);
        Assert.Equal(rows.Select(row => row.Token), set.Members.Select(member => member.Token));
        Assert.Equal(2, set.Similarity.Count(edge => edge.Kind == NearRelationshipKind.Near));
        Assert.Equal(4, set.Similarity.Count(edge => edge.Kind == NearRelationshipKind.Uncertain));
    }

    [Fact]
    public void LookAlikeSetsDoNotDependOnInputOrder()
    {
        // Two dense families that are unrelated to each other, plus scattered hashes that
        // bridge some of them, so merges compete and ties have to break the same way.
        var hashes = DenseHashes(20)
            .Concat(DenseHashes(20).Select(hash => ~hash))
            .Concat(Enumerable.Range(1, 10).Select(index => DenseHashes(20)[index] | (0xFFUL << (index * 5))))
            .Concat(SpreadHashes(20))
            .ToArray();
        var forward = ExactRows(hashes.Length);
        var reverse = ExactRows(hashes.Length);
        var forwardRows = StoreFixture.Snapshot(forward).Occurrences;
        var reverseRows = StoreFixture.Snapshot(reverse).Occurrences;

        Assert.True(forward.CommitNearDuplicates(new(forwardRows[0].Token.Pin, "managed-dhash-bgra-v1",
            forwardRows.Select((row, index) => Success(row, hashes[index])).ToImmutableArray())).Accepted);
        Assert.True(reverse.CommitNearDuplicates(new(reverseRows[0].Token.Pin, "managed-dhash-bgra-v1",
            reverseRows.Select((row, index) => Success(row, hashes[index])).Reverse().ToImmutableArray())).Accepted);

        Assert.Equal(
            StoreFixture.Snapshot(forward).Groups.Select(SetShape),
            StoreFixture.Snapshot(reverse).Groups.Select(SetShape));
    }

    [Fact]
    public void ThreeHundredMutuallySimilarPhotosFormOneSet()
    {
        // Every pair is Uncertain and none is Near, so all 300 start as single photos and
        // complete linkage keeps merging until one set holds them all; each merge only
        // revisits the merged set's neighbors.
        var hashes = SpreadHashes(300);
        var store = ExactRows(hashes.Length);
        var rows = StoreFixture.Snapshot(store).Occurrences;

        Assert.True(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1",
            rows.Select((row, index) => Success(row, hashes[index])).ToImmutableArray())).Accepted);

        var set = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(CandidateCategory.Uncertain, set.Category);
        Assert.Equal(300, set.Members.Length);
        Assert.Equal(300 * 299 / 2, set.Similarity.Length);
        Assert.Null(set.KeeperOccurrenceId);
    }

    // Distinct three-bit hashes: any two differ by 2, 4 or 6 bits (Near or Uncertain).
    private static ulong[] DenseHashes(int count)
    {
        var triples = new List<ulong>();
        for (var a = 0; a < 64; a++)
        for (var b = a + 1; b < 64; b++)
        for (var c = b + 1; c < 64; c++)
            triples.Add((1UL << a) | (1UL << b) | (1UL << c));
        return Enumerable.Range(0, count).Select(index => triples[index * 997 % triples.Count]).ToArray();
    }

    // Distinct eight-bit hashes that share at most five bits: any two differ by 6 to 16 bits,
    // which is always Uncertain and never Near.
    private static ulong[] SpreadHashes(int count)
    {
        var random = new Random(20261017);
        var hashes = new List<ulong>();
        while (hashes.Count < count)
        {
            var hash = 0UL;
            while (BitOperations.PopCount(hash) < 8) hash |= 1UL << random.Next(64);
            if (hashes.TrueForAll(other => BitOperations.PopCount(hash ^ other) >= 6)) hashes.Add(hash);
        }
        return [.. hashes];
    }

    private static string SetShape(CandidateGroup group) =>
        $"{group.Category}:{group.Uncertain}:" +
        string.Join(",", group.Members.Select(row => row.Token.OccurrenceId)) + ":" +
        string.Join(";", group.Similarity.Select(edge =>
            $"{edge.Left.OccurrenceId}-{edge.Right.OccurrenceId}-{edge.Kind}"));

    [Theory]
    [InlineData("scan")]
    [InlineData("occurrence")]
    [InlineData("content")]
    [InlineData("analysis")]
    public void EveryStaleNearBatchIdentityRejectsWithoutEffect(string mismatch)
    {
        var store = ExactRows(2);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var valid = new NearDuplicateBatch(rows[0].Token.Pin, "managed-dhash-bgra-v1",
            [Success(rows[0], 0), Success(rows[1], 1)]);
        var before = StoreFixture.Snapshot(store);
        var stale = mismatch switch
        {
            "scan" => valid with { Pin = valid.Pin with { ScanId = Guid.NewGuid() } },
            "occurrence" => valid with { Results = valid.Results.SetItem(1,
                valid.Results[1] with { Token = valid.Results[1].Token with { OccurrenceRevision = 99 } }) },
            "content" => valid with { Results = valid.Results.SetItem(1,
                valid.Results[1] with { Content = new ContentRevision(valid.Results[1].Content!.PhysicalFileKey,
                    valid.Results[1].Content!.ByteLength, new string('F', 64)) }) },
            _ => valid with { Pin = valid.Pin with { AnalysisRevision = 99 } },
        };

        var result = store.CommitNearDuplicates(stale);
        var after = StoreFixture.Snapshot(store);

        Assert.False(result.Accepted);
        Assert.Equal(before.Occurrences, after.Occurrences);
        Assert.Equal(before.Groups.Select(GroupShape), after.Groups.Select(GroupShape));
        Assert.Equal(before.Counts, after.Counts);
    }

    [Fact]
    public void ChangedMaterialEvidenceAdvancesRevisionAndRetiresThePriorGroupRevision()
    {
        var store = ExactRows(2);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var initialBatch = new NearDuplicateBatch(rows[0].Token.Pin, "managed-dhash-bgra-v1",
            [Success(rows[0], 0), Success(rows[1], 1)]);
        Assert.True(store.CommitNearDuplicates(initialBatch).Accepted);
        var initial = Assert.Single(StoreFixture.Snapshot(store).Groups);

        var changedBatch = initialBatch with
        {
            Results = initialBatch.Results.SetItem(1, Success(rows[1], 3)),
        };
        Assert.True(store.CommitNearDuplicates(changedBatch).Accepted);
        var changed = Assert.Single(StoreFixture.Snapshot(store).Groups);

        Assert.Equal(initial.GroupId, changed.GroupId);
        Assert.Equal(initial.GroupRevision + 1, changed.GroupRevision);
        Assert.False(store.MatchesGroup(initial.GroupId, initial.GroupRevision));
        Assert.True(store.MatchesGroup(changed.GroupId, changed.GroupRevision));
    }

    [Fact]
    public void FeatureFailureIsExplicitAndDoesNotOmitUnrelatedRows()
    {
        var store = ExactRows(3);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var failure = new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure);
        var result = store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0), new NearFeatureResult(rows[1].Token, rows[1].Content, null, failure),
            Success(rows[2], 1)]));
        var snapshot = StoreFixture.Snapshot(store);

        Assert.True(result.Accepted);
        Assert.Equal(3, snapshot.Occurrences.Length);
        Assert.Equal(failure, snapshot.Occurrences[1].VisualFeatures);
        Assert.Equal(ScanStageState.Succeeded, snapshot.Occurrences[0].NearDuplicate.State);
        Assert.Equal(ScanStageState.Succeeded, snapshot.Occurrences[2].NearDuplicate.State);
        Assert.Equal(2, snapshot.Counts.Analyzed);
        Assert.Equal(1, snapshot.Counts.Failed);
    }

    [Fact]
    public void SharpnessReachesTheOccurrenceAndItsLookAlikeSet()
    {
        var store = ExactRows(2);
        var rows = StoreFixture.Snapshot(store).Occurrences;

        Assert.True(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0) with { Sharpness = 812.5 }, Success(rows[1], 1) with { Sharpness = 90 }])).Accepted);
        var snapshot = StoreFixture.Snapshot(store);

        Assert.Equal([812.5, 90], snapshot.Occurrences.Select(row => row.Sharpness));
        Assert.Equal([812.5, 90], Assert.Single(snapshot.Groups).Members.Select(row => row.Sharpness));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ImpossibleSharpnessRejectsTheBatch(double sharpness)
    {
        var store = ExactRows(2);
        var rows = StoreFixture.Snapshot(store).Occurrences;

        Assert.False(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0) with { Sharpness = sharpness }, Success(rows[1], 1)])).Accepted);
        Assert.All(StoreFixture.Snapshot(store).Occurrences, row => Assert.Null(row.Sharpness));
    }

    [Fact]
    public void SharpnessWithoutAnalyzedFeaturesRejectsTheBatch()
    {
        var store = ExactRows(2);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var failure = new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure);

        Assert.False(store.CommitNearDuplicates(new(rows[0].Token.Pin, "managed-dhash-bgra-v1", [
            Success(rows[0], 0), new NearFeatureResult(rows[1].Token, rows[1].Content, null, failure, 50)])).Accepted);
    }

    private static ScanStore ExactRows(int count)
    {
        var store = new ScanStore(ScanTestData.Context());
        for (var id = 1; id <= count; id++)
        {
            var work = StoreFixture.Register(store, id);
            var content = new ContentRevision(work.File.PhysicalFileKey!.Value, 100 + id,
                id.ToString("X64", CultureInfo.InvariantCulture));
            var facts = new ProtectedFileFacts(content.PhysicalFileKey, content.ByteLength,
                work.File.ResolvedPath, null, new AllocationEvidence(4096, 1, true));
            Assert.True(store.CommitSignature(new(work.Token, content, facts, facts)).Accepted);
        }
        return store;
    }

    private static NearFeatureResult Success(ScanFileOccurrence row, ulong hash) => new(row.Token, row.Content,
        new PerceptualSignature("managed-dhash-bgra-v1", hash, 100, 100, 100, 100),
        new StageOutcome(ScanStageState.Succeeded, ScanReason.None));

    private static string GroupShape(CandidateGroup group) =>
        $"{group.GroupId}:{group.GroupRevision}:{group.Category}:{group.Uncertain}:" +
        string.Join(",", group.Members.Select(row => row.Token.OccurrenceId));
}
