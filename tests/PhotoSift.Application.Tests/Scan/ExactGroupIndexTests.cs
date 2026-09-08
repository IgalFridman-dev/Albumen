using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ExactGroupIndexTests
{
    [Fact]
    public void CollisionBucketFindsSecondAndThirdEqualityWhenFirstDiffers()
    {
        var store = new ScanStore(ScanTestData.Context());
        var first = StoreFixture.Register(store, 1);
        var second = StoreFixture.Register(store, 2);
        var third = StoreFixture.Register(store, 3);
        store.CommitSignature(StoreFixture.Signature(first));
        var pair = Assert.Single(store.CommitSignature(StoreFixture.Signature(second)).Comparisons);
        Assert.True(store.CommitComparison(StoreFixture.Equality(pair, false)));
        Assert.Empty(StoreFixture.Snapshot(store).Groups);
        var comparisons = store.CommitSignature(StoreFixture.Signature(third)).Comparisons;
        Assert.Equal(2, comparisons.Length);
        foreach (var work in comparisons)
            Assert.True(store.CommitComparison(StoreFixture.Equality(work,
                work.Left.Token.OccurrenceId != 1 && work.Right.Token.OccurrenceId != 1)));
        var group = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal([2L, 3L], group.Members.Select(row => row.Token.OccurrenceId));
        Assert.False(group.Uncertain);
        Assert.Null(group.KeeperOccurrenceId);
        Assert.Equal(CandidateCategory.Exact, group.Category);
        Assert.All(group.Members, row => Assert.Equal(group.Pin, row.Token.Pin));
        Assert.Single(group.Equality);
        Assert.Equal(4096, group.PotentialRecovery.Bytes);
        Assert.Equal(group.GroupId, Assert.Single(StoreFixture.Snapshot(store).Groups).GroupId);
    }

    [Fact]
    public void AliasesAloneNeverGroupAndLaterAliasesJoinWithoutExtraPhysicalCopies()
    {
        var store = new ScanStore(ScanTestData.Context());
        foreach (var id in new[] { 1L, 2L })
            Assert.Empty(store.CommitSignature(StoreFixture.Signature(StoreFixture.Register(store, id, 1))).Comparisons);
        Assert.Empty(StoreFixture.Snapshot(store).Groups);
        var distinct = StoreFixture.Register(store, 3);
        var comparison = Assert.Single(store.CommitSignature(StoreFixture.Signature(distinct)).Comparisons);
        store.CommitComparison(StoreFixture.Equality(comparison));
        var group = Assert.Single(StoreFixture.Snapshot(store).Groups);
        var alias = StoreFixture.Register(store, 4, 1);
        Assert.Empty(store.CommitSignature(StoreFixture.Signature(alias)).Comparisons);
        var updated = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(group.GroupId, updated.GroupId);
        Assert.Equal(4, updated.Members.Length);
        Assert.Equal(2, updated.Members.Select(row => row.PhysicalFileKey).Distinct().Count());
    }

    [Fact]
    public void AliasContentDisagreementInvalidatesAllOldEvidenceForThatPhysicalFile()
    {
        var (store, _, _) = VerifiedPair();
        var alias = StoreFixture.Register(store, 3, 1);
        var evidence = StoreFixture.Signature(alias);
        store.CommitSignature(evidence with { Content = new ContentRevision(evidence.Content.PhysicalFileKey, 1, new string('B', 64)) });
        var snapshot = StoreFixture.Snapshot(store);
        Assert.Empty(snapshot.Groups);
        Assert.All(snapshot.Occurrences.Where(row => row.PhysicalFileKey == alias.File.PhysicalFileKey),
            row => { Assert.Equal(ScanReason.Changed, row.Exact.Reason); Assert.Null(row.Content); });
        Assert.Equal(1, snapshot.Counts.Analyzed);
        Assert.Equal(2, snapshot.Counts.Failed);
    }

    [Fact]
    public void ComparisonReopenFailureRemovesFailingPhysicalMembershipAndPreservesPeer()
    {
        var (store, first, second) = VerifiedPair();
        var third = StoreFixture.Register(store, 3);
        var pending = Assert.Single(store.CommitSignature(StoreFixture.Signature(third)).Comparisons);
        var failing = pending.Left.Token == third.Token ? pending.Right.Token : pending.Left.Token;
        Assert.True(store.Fail(failing, ScanTestData.ReadFailure));
        Assert.False(store.Fail(failing, ScanTestData.ReadFailure));
        Assert.False(store.CommitComparison(StoreFixture.Equality(pending)));
        var snapshot = StoreFixture.Snapshot(store);
        Assert.Empty(snapshot.Groups);
        Assert.Equal(2, snapshot.Counts.Analyzed);
        var peer = failing == first.Token ? second.Token : first.Token;
        Assert.Equal(ScanStageState.Succeeded, snapshot.Occurrences.Single(row => row.Token == peer).Exact.State);
    }

    [Fact]
    public void CancellationSettlesComparisonWithoutInventingChangedContent()
    {
        var store = new ScanStore(ScanTestData.Context());
        var first = StoreFixture.Register(store, 1);
        store.CommitSignature(StoreFixture.Signature(first));
        var second = StoreFixture.Register(store, 2);
        var comparison = Assert.Single(store.CommitSignature(StoreFixture.Signature(second)).Comparisons);
        Assert.True(store.Fail(second.Token, ScanTestData.Canceled));
        Assert.False(store.CommitComparison(StoreFixture.Equality(comparison)));
        Assert.Equal(2, StoreFixture.Snapshot(store).Counts.Analyzed);
        Assert.All(StoreFixture.Snapshot(store).Occurrences, row => Assert.NotNull(row.Content));
    }

    [Fact]
    public void FinalizationReconcilesUnstartedTicketsAfterOneComparisonIsCanceled()
    {
        var store = new ScanStore(ScanTestData.Context());
        store.CommitSignature(StoreFixture.Signature(StoreFixture.Register(store, 1)));
        var second = Assert.Single(store.CommitSignature(StoreFixture.Signature(StoreFixture.Register(store, 2))).Comparisons);
        store.CommitComparison(StoreFixture.Equality(second, false));
        var pending = store.CommitSignature(StoreFixture.Signature(StoreFixture.Register(store, 3))).Comparisons;
        Assert.Equal(2, pending.Length);
        Assert.True(store.Fail(pending[0].Right.Token, ScanTestData.Canceled));
        Assert.Equal(1, store.PendingComparisons);
        store.Reconcile(ScanTestData.Canceled);
        Assert.Equal(0, store.PendingComparisons);
        Assert.True(store.Incomplete);
        Assert.Equal(3, StoreFixture.Snapshot(store).Counts.Analyzed);
        Assert.All(pending, work => Assert.False(store.CommitComparison(StoreFixture.Equality(work))));
    }

    [Fact]
    public void ForgedOrDuplicateComparisonCannotCreateEquality()
    {
        var store = new ScanStore(ScanTestData.Context());
        var first = StoreFixture.Register(store, 1);
        store.CommitSignature(StoreFixture.Signature(first));
        var second = StoreFixture.Register(store, 2);
        var pair = Assert.Single(store.CommitSignature(StoreFixture.Signature(second)).Comparisons);
        var forged = pair with { Left = pair.Left with { Token = pair.Left.Token with { Attempt = 99 } } };
        Assert.False(store.CommitComparison(StoreFixture.Equality(forged)));
        Assert.Empty(StoreFixture.Snapshot(store).Groups);
        Assert.True(store.CommitComparison(StoreFixture.Equality(pair)));
        Assert.False(store.CommitComparison(StoreFixture.Equality(pair)));
        Assert.Single(Assert.Single(StoreFixture.Snapshot(store).Groups).Equality);
    }

    [Fact]
    public void AllocationInstabilityMakesRecoveryUnavailableWithoutInvalidatingBytes()
    {
        var store = new ScanStore(ScanTestData.Context());
        var first = StoreFixture.Register(store, 1);
        store.CommitSignature(StoreFixture.Signature(first));
        var second = StoreFixture.Register(store, 2);
        var pair = Assert.Single(store.CommitSignature(StoreFixture.Signature(second)).Comparisons);
        var evidence = StoreFixture.Equality(pair);
        Assert.True(store.CommitComparison(evidence with { LeftFinal = evidence.LeftFinal with
        { Allocation = new AllocationEvidence(8192, 1, false), LastWriteUtc = DateTimeOffset.UnixEpoch.AddDays(1) } }));
        Assert.Equal(2, StoreFixture.Snapshot(store).Counts.Analyzed);
        Assert.Null(Assert.Single(StoreFixture.Snapshot(store).Groups).PotentialRecovery.Bytes);
    }

    private static (ScanStore Store, OccurrenceWork First, OccurrenceWork Second) VerifiedPair()
    {
        var store = new ScanStore(ScanTestData.Context());
        var first = StoreFixture.Register(store, 1);
        store.CommitSignature(StoreFixture.Signature(first));
        var second = StoreFixture.Register(store, 2);
        var pair = Assert.Single(store.CommitSignature(StoreFixture.Signature(second)).Comparisons);
        Assert.True(store.CommitComparison(StoreFixture.Equality(pair)));
        return (store, first, second);
    }
}
