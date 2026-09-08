using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ScanStoreTests
{
    [Theory]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("attempt")]
    public void EveryObsoleteIdentityRejectsSignatureAndFailure(string mismatch)
    {
        var store = new ScanStore(ScanTestData.Context());
        var work = StoreFixture.Register(store, 1);
        var token = mismatch switch
        {
            "scan" => work.Token with { Pin = work.Token.Pin with { ScanId = Guid.NewGuid() } },
            "scope" => work.Token with { Pin = work.Token.Pin with { ScopeRevision = 99 } },
            "rules" => work.Token with { Pin = work.Token.Pin with { RuleSetRevision = 99 } },
            "analysis" => work.Token with { Pin = work.Token.Pin with { AnalysisRevision = 99 } },
            "id" => work.Token with { OccurrenceId = 99 },
            "revision" => work.Token with { OccurrenceRevision = 99 },
            _ => work.Token with { Attempt = 99 },
        };
        Assert.False(store.CommitSignature(StoreFixture.Signature(work) with { Token = token }).Accepted);
        Assert.False(store.Fail(token, ScanTestData.ReadFailure));
        Assert.Equal(ScanStageState.Running, StoreFixture.Snapshot(store).Occurrences.Single().Exact.State);
    }

    [Fact]
    public void DuplicateSignatureAndFailureWithoutPendingComparisonCannotChangeSuccess()
    {
        var store = new ScanStore(ScanTestData.Context());
        var work = StoreFixture.Register(store, 1);
        Assert.True(store.CommitSignature(StoreFixture.Signature(work)).Accepted);
        Assert.False(store.CommitSignature(StoreFixture.Signature(work)).Accepted);
        Assert.False(store.Fail(work.Token, ScanTestData.ReadFailure));
        Assert.Equal(new ScanCounts(1, 1, 0, 0, 0, 0), StoreFixture.Snapshot(store).Counts);
    }

    [Fact]
    public void TerminalRowsRejectSignaturesAndCountsAreDerivedFromOutcomes()
    {
        var store = new ScanStore(ScanTestData.Context());
        var failed = StoreFixture.Register(store, 1);
        Assert.True(store.Fail(failed.Token, ScanTestData.ReadFailure));
        Assert.False(store.CommitSignature(StoreFixture.Signature(failed)).Accepted);
        var canceled = StoreFixture.Register(store, 2);
        Assert.True(store.Fail(canceled.Token, ScanTestData.Canceled));
        var excluded = ScanTestData.Work(3).File with
        { Outcome = new StageOutcome(ScanStageState.Unsupported, ScanReason.UnsupportedExtension) };
        Assert.Null(store.Register(excluded));
        StoreFixture.Register(store, 4);
        Assert.Equal(new ScanCounts(4, 0, 1, 1, 1, 1), StoreFixture.Snapshot(store).Counts);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("length")]
    [InlineData("path")]
    public void InvalidProtectedFactsCannotProduceSuccessfulSignature(string mismatch)
    {
        var store = new ScanStore(ScanTestData.Context());
        var work = StoreFixture.Register(store, 1);
        var evidence = StoreFixture.Signature(work);
        var final = mismatch switch
        {
            "identity" => evidence.Final with { PhysicalFileKey = new PhysicalFileKey(9, new FileId128(0, 9)) },
            "length" => evidence.Final with { ByteLength = 9 },
            _ => evidence.Final with { ResolvedPath = @"C:\Outside\1.jpg" },
        };
        store.CommitSignature(evidence with { Final = final });
        Assert.Equal(0, StoreFixture.Snapshot(store).Counts.Analyzed);
        Assert.Empty(StoreFixture.Snapshot(store).Groups);
    }

    [Fact]
    public void SnapshotsRemainImmutableAfterLaterCommits()
    {
        var store = new ScanStore(ScanTestData.Context());
        var work = StoreFixture.Register(store, 1);
        var before = StoreFixture.Snapshot(store);
        store.CommitSignature(StoreFixture.Signature(work));
        Assert.Equal(ScanStageState.Running, before.Occurrences.Single().Exact.State);
        Assert.Equal(0, before.Counts.Analyzed);
        Assert.Equal(1, StoreFixture.Snapshot(store).Counts.Analyzed);
    }
}

internal static class StoreFixture
{
    public static OccurrenceWork Register(ScanStore store, long id, ulong? physical = null)
    {
        var file = ScanTestData.Work(id).File;
        if (physical.HasValue) file = file with { PhysicalFileKey = new PhysicalFileKey(1, new FileId128(0, physical.Value)) };
        return Assert.IsType<OccurrenceWork>(store.Register(file));
    }
    public static ProtectedFileFacts Facts(OccurrenceWork work) => new(work.File.PhysicalFileKey!.Value,
        1, work.File.ResolvedPath, DateTimeOffset.UnixEpoch, new AllocationEvidence(4096, 1, true));
    public static SignatureEvidence Signature(OccurrenceWork work) => new(work.Token,
        new ContentRevision(work.File.PhysicalFileKey!.Value, 1, ScanTestData.Digest), Facts(work), Facts(work));
    public static ComparisonEvidence Equality(ComparisonWork work, bool equal = true) => new(work, equal,
        Facts(work.Left), Facts(work.Right), new StreamEquality(equal,
            new StreamDigest(1, ScanTestData.Digest), new StreamDigest(1, ScanTestData.Digest)));
    public static ScanSnapshot Snapshot(ScanStore store) => store.Snapshot(ScanStatus.Running,
        new StageOutcome(ScanStageState.Running, ScanReason.None));
}
