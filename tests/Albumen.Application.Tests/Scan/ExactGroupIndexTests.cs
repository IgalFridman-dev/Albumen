using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ExactGroupIndexTests
{
    [Fact]
    public void IdenticalProjectionRetainsIdentityWithoutRevisionDrift()
    {
        var index = new ExactGroupIndex();
        var initial = TestGroups.Publish(index, TestGroups.Component(1, 2));
        var unchanged = TestGroups.Publish(index, TestGroups.Component(2, 1));

        Assert.Equal(initial.GroupId, unchanged.GroupId);
        Assert.Equal(1, initial.GroupRevision);
        Assert.Equal(1, unchanged.GroupRevision);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("equality")]
    [InlineData("uncertainty")]
    [InlineData("keeper")]
    [InlineData("recovery")]
    public void EachMaterialProjectionChangeAdvancesRevisionExactlyOnce(string change)
    {
        var index = new ExactGroupIndex();
        var projection = TestGroups.Component(1, 2, 3);
        var initial = TestGroups.Publish(index, projection);
        var changedProjection = change switch
        {
            "members" => projection with
            {
                Members = projection.Members.Add(TestGroups.Row(4, 1, @"C:\Photos\alias.jpg")),
            },
            "equality" => projection with
            {
                Equality = projection.Equality.Add(TestGroups.Edge(projection.Members[0], projection.Members[2])),
            },
            "uncertainty" => projection with { Uncertain = true },
            "keeper" => projection with { KeeperOccurrenceId = 2 },
            _ => projection with { PotentialRecovery = new PotentialRecovery(4096, RecoveryUnavailableReason.None) },
        };

        var changed = TestGroups.Publish(index, changedProjection);
        var neutral = TestGroups.Publish(index, changedProjection);

        Assert.Equal(initial.GroupId, changed.GroupId);
        Assert.Equal(2, changed.GroupRevision);
        Assert.Equal(changed.GroupId, neutral.GroupId);
        Assert.Equal(2, neutral.GroupRevision);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("attempt")]
    public void OccurrenceTokenRevisionOrAttemptChangeAdvancesRevision(string change)
    {
        var index = new ExactGroupIndex();
        var projection = TestGroups.Component(1, 2);
        var initial = TestGroups.Publish(index, projection);
        var member = projection.Members[0];
        var changedToken = change == "revision"
            ? member.Token with { OccurrenceRevision = 2 }
            : member.Token with { Attempt = 2 };
        var changedProjection = projection with
        {
            Members = projection.Members.SetItem(0, TestGroups.Copy(member, token: changedToken)),
        };

        var changed = TestGroups.Publish(index, changedProjection);

        Assert.Equal(initial.GroupId, changed.GroupId);
        Assert.Equal(2, changed.GroupRevision);
    }

    [Fact]
    public void ContentRevisionChangeAdvancesRevision()
    {
        var index = new ExactGroupIndex();
        var projection = TestGroups.Component(1, 2);
        var initial = TestGroups.Publish(index, projection);
        var member = projection.Members[0];
        var changedContent = new ContentRevision(member.PhysicalFileKey!.Value, 2, ScanTestData.Digest);
        var changedProjection = projection with
        {
            Members = projection.Members.SetItem(0, TestGroups.Copy(member, content: changedContent)),
        };

        var changed = TestGroups.Publish(index, changedProjection);

        Assert.Equal(initial.GroupId, changed.GroupId);
        Assert.Equal(2, changed.GroupRevision);
    }

    [Fact]
    public void AllocationEvidenceChangeAdvancesRevision()
    {
        var index = new ExactGroupIndex();
        var projection = TestGroups.Component(1, 2);
        var initial = TestGroups.Publish(index, projection);
        var member = projection.Members[0];
        var changedProjection = projection with
        {
            Members = projection.Members.SetItem(0, TestGroups.Copy(
                member,
                allocation: new AllocationEvidence(8192, 1, true))),
        };

        var changed = TestGroups.Publish(index, changedProjection);

        Assert.Equal(initial.GroupId, changed.GroupId);
        Assert.Equal(2, changed.GroupRevision);
    }

    [Fact]
    public void CaseEquivalentPresentationPathsDoNotAdvanceRevision()
    {
        var index = new ExactGroupIndex();
        var projection = TestGroups.Component(1, 2);
        var initial = TestGroups.Publish(index, projection);
        var changedPaths = projection with
        {
            Members = projection.Members.Select(row => new ScanFileOccurrence(
                row.Token,
                row.DiscoveredPath.ToUpperInvariant(),
                row.ResolvedPath.ToUpperInvariant(),
                row.PhysicalFileKey,
                row.Enumerate,
                row.Exact,
                row.Content,
                row.Allocation)).ToImmutableArray(),
        };

        var unchanged = TestGroups.Publish(index, changedPaths);

        Assert.Equal(initial.GroupId, unchanged.GroupId);
        Assert.Equal(initial.GroupRevision, unchanged.GroupRevision);
    }

    [Fact]
    public void MergeRetiresBothInputsAndStartsNewIdentity()
    {
        var index = new ExactGroupIndex();
        var initial = TestGroups.PublishMany(index, TestGroups.Component(1, 2), TestGroups.Component(3, 4));

        var merged = TestGroups.Publish(index, TestGroups.Component(1, 2, 3, 4));

        Assert.DoesNotContain(merged.GroupId, initial.Select(group => group.GroupId));
        Assert.Equal(1, merged.GroupRevision);
        Assert.All(initial, group => Assert.False(index.IsCurrent(group.GroupId, group.GroupRevision)));
    }

    [Fact]
    public void SplitRetiresInputAndStartsNewIdentities()
    {
        var index = new ExactGroupIndex();
        var initial = TestGroups.Publish(index, TestGroups.Component(1, 2, 3, 4));

        var split = TestGroups.PublishMany(index, TestGroups.Component(1, 2), TestGroups.Component(3, 4));

        Assert.All(split, group =>
        {
            Assert.NotEqual(initial.GroupId, group.GroupId);
            Assert.Equal(1, group.GroupRevision);
        });
        Assert.False(index.IsCurrent(initial.GroupId, initial.GroupRevision));
    }

    [Fact]
    public void DisappearanceRetiresIdentityBeforeEquivalentComponentReturns()
    {
        var index = new ExactGroupIndex();
        var initial = TestGroups.Publish(index, TestGroups.Component(1, 2));
        Assert.Empty(index.Publish(TestGroups.Pin, []));

        var returned = TestGroups.Publish(index, TestGroups.Component(1, 2));

        Assert.NotEqual(initial.GroupId, returned.GroupId);
        Assert.Equal(1, returned.GroupRevision);
        Assert.False(index.IsCurrent(initial.GroupId, initial.GroupRevision));
    }

    [Fact]
    public void AmbiguousReplacementRetiresSourceRatherThanRedirectingIt()
    {
        var index = new ExactGroupIndex();
        var initial = TestGroups.Publish(index, TestGroups.Component(1, 2));

        var replacements = TestGroups.PublishMany(index, TestGroups.Component(1, 3), TestGroups.Component(2, 4));

        Assert.All(replacements, group => Assert.NotEqual(initial.GroupId, group.GroupId));
        Assert.False(index.IsCurrent(initial.GroupId, initial.GroupRevision));
    }

    [Fact]
    public void RevisionOverflowIsVisibleProgrammingFault()
    {
        Assert.Throws<OverflowException>(() => ExactGroupIndex.AdvanceRevision(long.MaxValue));
    }

    [Fact]
    public void LaterRevisionOverflowLeavesEveryPreviousPublicationCurrent()
    {
        var advances = 0;
        var index = new ExactGroupIndex(revision => ++advances == 2
            ? ExactGroupIndex.AdvanceRevision(long.MaxValue)
            : ExactGroupIndex.AdvanceRevision(revision));
        var firstProjection = TestGroups.Component(1, 2);
        var secondProjection = TestGroups.Component(3, 4);
        var initial = TestGroups.PublishMany(index, firstProjection, secondProjection);

        Assert.Throws<OverflowException>(() => TestGroups.PublishMany(
            index,
            firstProjection with { Uncertain = true },
            secondProjection with { Uncertain = true }));

        Assert.All(initial, group => Assert.True(index.IsCurrent(group.GroupId, group.GroupRevision)));
        var unchanged = TestGroups.PublishMany(index, firstProjection, secondProjection);
        Assert.Equal(initial.Select(group => (group.GroupId, group.GroupRevision)),
            unchanged.Select(group => (group.GroupId, group.GroupRevision)));
    }

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
    public void SnapshotsWhileComparisonsSettleOnlyAdvanceForMaterialChange()
    {
        var (store, _, _) = VerifiedPair();
        var initial = Assert.Single(StoreFixture.Snapshot(store).Groups);
        var third = StoreFixture.Register(store, 3);
        var comparison = Assert.Single(store.CommitSignature(StoreFixture.Signature(third)).Comparisons);

        var pending = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(initial.GroupRevision, pending.GroupRevision);
        var stillPending = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(initial.GroupRevision, stillPending.GroupRevision);
        Assert.True(store.CommitComparison(StoreFixture.Equality(comparison)));

        var changed = Assert.Single(StoreFixture.Snapshot(store).Groups);
        Assert.Equal(initial.GroupId, changed.GroupId);
        Assert.Equal(initial.GroupRevision + 1, changed.GroupRevision);
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

internal static class TestGroups
{
    public static readonly ScanPin Pin = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, 1, 1);

    public static CandidateGroup Publish(ExactGroupIndex index, CandidateGroupProjection projection) =>
        Assert.Single(index.Publish(Pin, [projection]));

    public static ImmutableArray<CandidateGroup> PublishMany(
        ExactGroupIndex index,
        params CandidateGroupProjection[] projections) => index.Publish(Pin, [.. projections]);

    public static CandidateGroupProjection Component(params long[] ids)
    {
        var members = ids.Select(id => Row(id, (ulong)id, $@"C:\Photos\{id}.jpg")).ToImmutableArray();
        var equality = members.Zip(members.Skip(1), Edge).ToImmutableArray();
        return new CandidateGroupProjection(
            members.Select(row => row.PhysicalFileKey!.Value).ToImmutableArray(),
            CandidateCategory.Exact,
            members,
            equality,
            false,
            null,
            new PotentialRecovery(0, RecoveryUnavailableReason.None));
    }

    public static ScanFileOccurrence Row(long occurrenceId, ulong physicalId, string path)
    {
        var key = new PhysicalFileKey(1, new FileId128(0, physicalId));
        return new ScanFileOccurrence(
            new OccurrenceToken(Pin, occurrenceId, 1, 1),
            path,
            path,
            key,
            ScanTestData.Success,
            ScanTestData.Success,
            new ContentRevision(key, 1, ScanTestData.Digest),
            new AllocationEvidence(4096, 1, true));
    }

    public static ScanFileOccurrence Copy(
        ScanFileOccurrence row,
        OccurrenceToken? token = null,
        ContentRevision? content = null,
        AllocationEvidence? allocation = null) => new(
            token ?? row.Token,
            row.DiscoveredPath,
            row.ResolvedPath,
            row.PhysicalFileKey,
            row.Enumerate,
            row.Exact,
            content ?? row.Content,
            allocation ?? row.Allocation);

    public static EqualityEvidence Edge(ScanFileOccurrence left, ScanFileOccurrence right) =>
        new(left.Token, right.Token, left.Content!, right.Content!);
}
