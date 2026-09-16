using System.Collections.Immutable;
using System.Globalization;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class NearDuplicateGroupTests
{
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

    [Fact]
    public void StaleOccurrenceContentScanOrAnalysisRevisionRejectsWholeBatchWithoutEffect()
    {
        var store = ExactRows(2);
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var valid = new NearDuplicateBatch(rows[0].Token.Pin, "managed-dhash-bgra-v1",
            [Success(rows[0], 0), Success(rows[1], 1)]);
        var before = StoreFixture.Snapshot(store);
        var stale = valid with { Results = valid.Results.SetItem(1,
            valid.Results[1] with { Content = new ContentRevision(valid.Results[1].Content!.PhysicalFileKey,
                valid.Results[1].Content!.ByteLength, new string('F', 64)) }) };

        var result = store.CommitNearDuplicates(stale);
        var after = StoreFixture.Snapshot(store);

        Assert.False(result.Accepted);
        Assert.Equal(before.Occurrences, after.Occurrences);
        Assert.Equal(before.Groups.Select(GroupShape), after.Groups.Select(GroupShape));
        Assert.Equal(before.Counts, after.Counts);
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
