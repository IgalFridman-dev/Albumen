using System.Collections.Immutable;
using System.Globalization;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

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
    public void AtomicCommitKeepsExactDistinctAndPublishesDeterministicNearAndUncertainGroups()
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
        Assert.Contains(firstSnapshot.Groups, group => group.Category == CandidateCategory.NearDuplicate);
        Assert.Contains(firstSnapshot.Groups, group => group.Category == CandidateCategory.Uncertain && group.Uncertain);
        Assert.Equal(firstSnapshot.Groups.Select(GroupShape), secondSnapshot.Groups.Select(GroupShape));
        Assert.All(firstSnapshot.Groups.Where(group => group.Category != CandidateCategory.Exact),
            group => Assert.Null(group.KeeperOccurrenceId));
    }

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
