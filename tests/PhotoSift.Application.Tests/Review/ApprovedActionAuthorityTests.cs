using System.Collections.Immutable;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ApprovedActionAuthorityTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("time")]
    [InlineData("session")]
    [InlineData("selection")]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("root")]
    [InlineData("order")]
    [InlineData("ids")]
    [InlineData("group")]
    [InlineData("revision")]
    [InlineData("occurrence")]
    [InlineData("discovered")]
    [InlineData("resolved")]
    [InlineData("content")]
    [InlineData("allocation")]
    [InlineData("evidence")]
    [InlineData("disclosure")]
    public async Task EveryChangedPayloadFailsWithoutConsumingTheExactRequest(string change)
    {
        var reader = Reader();
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var request = await ApproveAsync(fixture);
        var item = request.Items[0];
        var changed = change switch
        {
            "id" => request with { RequestId = Guid.NewGuid() },
            "time" => request with { ApprovedUtc = request.ApprovedUtc.ToOffset(TimeSpan.FromHours(1)) },
            "session" => request with { ReviewSessionId = Guid.NewGuid() },
            "selection" => request with { SelectionRevision = 99 },
            "scan" => request with { Scan = request.Scan with { ScanId = Guid.NewGuid() } },
            "scope" => request with { Scan = request.Scan with { ScopeRevision = 99 } },
            "rules" => request with { Scan = request.Scan with { RuleSetRevision = 99 } },
            "analysis" => request with { Scan = request.Scan with { AnalysisRevision = 99 } },
            "root" => request with { Scope = request.Scope with { ResolvedPath = @"C:\Elsewhere" } },
            "order" => request with { Items = request.Items.Reverse().ToImmutableArray() },
            "ids" => request with { SelectedOccurrenceIds = [2, 1] },
            "disclosure" => request with { Disclosure = request.Disclosure with { FutureDestination = "Elsewhere" } },
            _ => request with { Items = request.Items.SetItem(0, change switch
            {
                "group" => item with { GroupId = Guid.NewGuid() },
                "revision" => item with { GroupRevision = 99 },
                "occurrence" => item with { Occurrence = item.Occurrence with { OccurrenceRevision = 99 } },
                "discovered" => item with { DiscoveredPath = @"C:\Photos\other.jpg" },
                "resolved" => item with { ResolvedPath = @"C:\Photos\other.jpg" },
                "content" => item with { Content = new(item.PhysicalFileKey, 2, ScanTestData.Digest) },
                "allocation" => item with { Allocation = item.Allocation with { LinkCount = 99 } },
                _ => item with { OverrideEvidenceId = new(Guid.NewGuid()) },
            }) },
        };
        var rejected = await ClaimAsync(fixture, changed);
        Assert.Equal(ApprovedActionClaimOutcome.Stale, rejected.Outcome);
        Assert.Empty(reader.Leases);
        var accepted = await ClaimAsync(fixture, request);
        Assert.Equal(ApprovedActionClaimOutcome.Claimed, accepted.Outcome);
        Assert.Same(request, accepted.Claim!.Request);
    }

    [Fact]
    public async Task EqualValueAndSimultaneousClaimsConvergeOnOneCapabilityAndProbe()
    {
        var entered = ScanTestData.Signal<bool>();
        var release = ScanTestData.Signal<StreamDigest>();
        var reader = Reader();
        var analyzer = new ScanTestAnalyzer { Hash = (_, _) => { entered.TrySetResult(true); return new(release.Task); } };
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, analyzer));
        var request = await ApproveAsync(fixture);
        var first = fixture.Owner.ClaimAsync(request, [], TestContext.Current.CancellationToken);
        await fixture.PumpAsync(entered.Task);
        var duplicate = fixture.Owner.ClaimAsync(request with { Items = [.. request.Items], SelectedOccurrenceIds = [.. request.SelectedOccurrenceIds] }, [], TestContext.Current.CancellationToken);
        var conflict = await ClaimAsync(fixture, request with { Disclosure = request.Disclosure with { SelectedOccurrenceCount = 999 } });
        Assert.Equal(ApprovedActionClaimOutcome.Stale, conflict.Outcome);
        Assert.False(first.IsCompleted);
        Assert.False(duplicate.IsCompleted);
        release.TrySetResult(new(1, ScanTestData.Digest));
        await fixture.PumpAsync(Task.WhenAll(first, duplicate));
        Assert.Equal(ApprovedActionClaimOutcome.Claimed, (await first).Outcome);
        Assert.Same((await first).Claim, (await duplicate).Claim);
        var repeated = await ClaimAsync(fixture, request);
        Assert.Equal(ApprovedActionClaimOutcome.Existing, repeated.Outcome);
        Assert.Same((await first).Claim, repeated.Claim);
        Assert.Equal(3, reader.Leases.Count);
        Assert.All(reader.Leases, lease => Assert.True(lease.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrChangedUnselectedWitnessBlocksTheWholeClaim(bool changed)
    {
        var reader = Reader();
        var normal = reader.Create!;
        reader.Create = (index, request) => request.ResolvedPath.EndsWith("3.jpg", StringComparison.Ordinal)
            ? changed ? new ScanTestLease(request) { OpenedFacts = new(request.ExpectedPhysicalFileKey, 99, request.ResolvedPath, null, new(4096, 1, true)) }
                : throw new FileNotFoundException()
            : normal(index, request);
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var result = await ClaimAsync(fixture, await ApproveAsync(fixture));
        Assert.Equal(ApprovedActionClaimOutcome.UnsafeCurrentFacts, result.Outcome);
        Assert.Equal(changed ? ActionSafetyReason.Changed : ActionSafetyReason.Missing, result.Reason);
        Assert.All(reader.Leases, lease => Assert.True(lease.Disposed));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("payload")]
    [InlineData("extra")]
    public async Task EveryOverrideReadMustMatchTheAttachedCompletePayload(string change)
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store, actionSafety: new(Reader(), new ScanTestAnalyzer()));
        await fixture.OpenOverrideSelectedAsync();
        var request = (await fixture.ApproveAsync(fixture.ApprovalCommand())).ApprovedRequest!;
        var exact = new OverrideReadResult(OverrideReadOutcome.Found, fixture.OverrideEvidence);
        ImmutableArray<OverrideReadResult> reads = change switch
        {
            "missing" => [],
            "extra" => [exact, exact],
            _ => [exact with { Evidence = fixture.OverrideEvidence! with { DeliberatelyAffirmed = false } }],
        };
        Assert.Equal(ApprovedActionClaimOutcome.EvidenceUnavailable, (await ClaimAsync(fixture, request, reads)).Outcome);
        Assert.Equal(ApprovedActionClaimOutcome.Claimed, (await ClaimAsync(fixture, request, [exact])).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementOrFaultDuringHashingRejectsCompletionAndRetainsLeases(bool fault)
    {
        var entered = ScanTestData.Signal<bool>();
        var release = ScanTestData.Signal<StreamDigest>();
        var reader = Reader();
        var analyzer = new ScanTestAnalyzer { Hash = (_, _) => { entered.TrySetResult(true); return new(release.Task); } };
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, analyzer));
        var request = await ApproveAsync(fixture);
        var pending = fixture.Owner.ClaimAsync(request, [], TestContext.Current.CancellationToken);
        await fixture.PumpAsync(entered.Task);
        if (fault) await fixture.PumpAsync(fixture.Owner.ReportObserverFaultAsync(fixture.Owner.Current.Pin!));
        else await fixture.OpenAsync();
        Assert.All(reader.Leases, lease => Assert.False(lease.Disposed));
        release.TrySetResult(new(1, ScanTestData.Digest));
        await fixture.PumpAsync(pending);
        Assert.Equal(fault ? ApprovedActionClaimOutcome.Faulted : ApprovedActionClaimOutcome.Stale, (await pending).Outcome);
        Assert.All(reader.Leases, lease => Assert.True(lease.Disposed));
        if (!fault) Assert.Null(fixture.Owner.Current.ApprovedRequest);
    }

    [Fact]
    public async Task RefreshUsesClaimAndFreshDependentGroupWithPriorRecycledPathAbsent()
    {
        var reader = Reader();
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var request = await ApproveAsync(fixture);
        var claim = (await ClaimAsync(fixture, request)).Claim!;
        var outcome = new PriorActionOutcome(request.Items[0].GroupId, 1, PriorActionDisposition.Recycled);
        var refresh = fixture.Owner.RefreshSafetyAsync(claim, request.Items[1], [outcome], TestContext.Current.CancellationToken);
        await fixture.PumpAsync(refresh);
        var result = await refresh;
        Assert.Equal(ApprovedActionClaimOutcome.Claimed, result.Outcome);
        Assert.Same(claim, result.Envelope!.Claim);
        Assert.Same(request.Items[1], result.Envelope.Item);
        Assert.Equal([2L, 3L], result.Envelope.Observations.Select(o => o.ApprovedOccurrence.Token.OccurrenceId));
        Assert.Equal([2L], result.Envelope.Input.RemainingApprovedOccurrenceIds);
        Assert.Equal(5, reader.Leases.Count);
        Assert.Equal(ApprovedActionClaimOutcome.Existing, (await ClaimAsync(fixture, request)).Outcome);
    }

    [Fact]
    public async Task UnknownDependentOutcomeAndForgedCapabilityCannotRefresh()
    {
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(Reader(), new ScanTestAnalyzer()));
        var request = await ApproveAsync(fixture);
        var claim = (await ClaimAsync(fixture, request)).Claim!;
        var unknown = fixture.Owner.RefreshSafetyAsync(claim, request.Items[1], [new(request.Items[0].GroupId, 1, PriorActionDisposition.Unknown)], TestContext.Current.CancellationToken);
        await fixture.PumpAsync(unknown);
        Assert.Equal(ActionSafetyReason.DependentOutcomeUnknown, (await unknown).Reason);
        var forged = fixture.Owner.RefreshSafetyAsync(claim with { ClaimId = new(Guid.NewGuid()) }, request.Items[0], [], TestContext.Current.CancellationToken);
        await fixture.PumpAsync(forged);
        Assert.Equal(ApprovedActionClaimOutcome.Stale, (await forged).Outcome);
    }

    internal static ScanTestReader Reader() => new()
    {
        Create = (_, request) => new ScanTestLease(request)
        {
            OpenedFacts = new(request.ExpectedPhysicalFileKey, 1, request.ResolvedPath, null,
                new(4096, request.ExpectedPhysicalFileKey.FileId.Low == 1 ? 2u : 1u, true)),
        },
    };

    internal static async Task<ApprovedActionRequest> ApproveAsync(ApprovalFixture fixture)
    {
        await fixture.OpenSelectedAsync();
        return (await fixture.ApproveAsync(fixture.ApprovalCommand())).ApprovedRequest!;
    }

    internal static async Task<ApprovedActionClaimResult> ClaimAsync(ApprovalFixture fixture,
        ApprovedActionRequest request, ImmutableArray<OverrideReadResult> reads = default)
    {
        var pending = ((IApprovedActionAuthority)fixture.Owner).ClaimAsync(request, reads.IsDefault ? [] : reads, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(pending);
        return await pending;
    }
}
