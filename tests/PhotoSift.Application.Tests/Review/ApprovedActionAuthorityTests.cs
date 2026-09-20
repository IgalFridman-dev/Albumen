using System.Collections.Immutable;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ApprovedActionAuthorityTests
{
    [Fact]
    public async Task ActionCancellationCallbacksRunOutsideOwnerAndDrainBeforeEffectSettlement()
    {
        var reader = Reader();
        var analyzer = new ScanTestAnalyzer();
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, analyzer));
        var request = await ApproveAsync(fixture);
        var entered = ScanTestData.Signal<bool>();
        var callbackEntered = ScanTestData.Signal<bool>();
        using var releaseCallback = new ManualResetEventSlim();
        var registration = default(CancellationTokenRegistration);
        analyzer.Hash = async (_, token) =>
        {
            registration = token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                releaseCallback.Wait(TimeSpan.FromSeconds(5));
            });
            entered.TrySetResult(true);
            return await new TaskCompletionSource<StreamDigest>(TaskCreationOptions.RunContinuationsAsynchronously)
                .Task.WaitAsync(TimeSpan.FromSeconds(3), token);
        };
        var pending = ClaimOutcomeAsync(fixture.Owner, request);
        await fixture.PumpAsync(entered.Task);
        var dispose = fixture.Owner.DisposeAsync().AsTask();
        try
        {
            await fixture.PumpAsync(Task.WhenAny(callbackEntered.Task, pending));
            Assert.True(callbackEntered.Task.IsCompletedSuccessfully, "Owner cancellation callback was never invoked.");
            var command = fixture.Owner.ChangeSelectionAsync(fixture.SelectionCommand(0,
                SelectionCommandKind.ReplaceFocusedGroup, 1, 2), TestContext.Current.CancellationToken);
            await fixture.PumpAsync(command);
            Assert.Equal(ReviewCommandOutcome.Stale, (await command).Outcome);
            Assert.False(dispose.IsCompleted);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            releaseCallback.Set();
            await fixture.PumpAsync(Task.WhenAll(pending, dispose));
            await registration.DisposeAsync();
        }
        Assert.Equal(ApprovedActionClaimOutcome.Stale, await pending);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(false, "replace")]
    [InlineData(false, "fault")]
    [InlineData(false, "dispose")]
    [InlineData(true, "replace")]
    [InlineData(true, "fault")]
    [InlineData(true, "dispose")]
    public async Task OwnerCancelsCooperativeActionHashAfterReviewRunAlreadyFinished(bool refresh, string transition)
    {
        var reader = Reader();
        var analyzer = new ScanTestAnalyzer();
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, analyzer));
        var request = await ApproveAsync(fixture);
        var claim = refresh ? (await ClaimAsync(fixture, request)).Claim : null;
        var finished = ScanTestData.Signal<bool>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Sealed) finished.TrySetResult(true);
        };
        fixture.Executor.Runs.Single().Done.TrySetResult();
        await fixture.PumpAsync(finished.Task);
        var entered = ScanTestData.Signal<bool>();
        var canceled = ScanTestData.Signal<ReviewSnapshot>();
        var registration = default(CancellationTokenRegistration);
        analyzer.Hash = async (_, token) =>
        {
            // Retain this observer until effect settlement. Hash completion can
            // otherwise unregister it while CancelAsync is still dispatching.
            registration = token.Register(() => canceled.TrySetResult(fixture.Owner.Current));
            entered.TrySetResult(true);
            // Deadline only bounds a broken implementation's test failure. No
            // test path manually completes or releases this cooperative worker.
            return await new TaskCompletionSource<StreamDigest>(TaskCreationOptions.RunContinuationsAsynchronously)
                .Task.WaitAsync(TimeSpan.FromSeconds(3), token);
        };
        var pending = refresh
            ? RefreshOutcomeAsync(fixture.Owner, claim!, request.Items[0])
            : ClaimOutcomeAsync(fixture.Owner, request);
        await fixture.PumpAsync(entered.Task);
        Assert.Equal(1, reader.Live);
        var oldPin = fixture.Owner.Current.Pin;
        Task change = transition switch
        {
            "replace" => fixture.Owner.OpenAsync(fixture.Request, TestContext.Current.CancellationToken),
            "fault" => fixture.Owner.ReportObserverFaultAsync(oldPin!),
            _ => fixture.Owner.DisposeAsync().AsTask(),
        };
        await fixture.PumpAsync(Task.WhenAll(pending, change));
        await registration.DisposeAsync();
        Assert.True(canceled.Task.IsCompletedSuccessfully, "The owner did not cancel the cooperative Action hash.");
        var snapshotAtCancellation = await canceled.Task;
        if (transition == "replace") Assert.NotEqual(oldPin, snapshotAtCancellation.Pin);
        if (transition == "fault") Assert.Equal(ReviewStatus.Faulted, snapshotAtCancellation.Status);
        Assert.Equal(transition == "fault" ? ApprovedActionClaimOutcome.Faulted : ApprovedActionClaimOutcome.Stale, await pending);
        Assert.Equal(0, reader.Live);
        Assert.All(reader.Leases, lease => Assert.True(lease.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisappearingExternalSurvivorBlocksClaimAndRefresh(bool refresh)
    {
        var reader = new ScanTestReader();
        uint currentLinks = 2;
        reader.Create = (_, open) => new ScanTestLease(open)
        {
            OpenedFacts = new(open.ExpectedPhysicalFileKey, 1, open.ResolvedPath, null, new(4096, currentLinks, true)),
        };
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var members = new[]
            {
                fixture.Request.Scan.Groups[0].Members[0],
                fixture.Request.Scan.Groups[0].Members[2],
            }
            .Select(member => ReviewFixture.Copy(member, allocation: new(4096, 2, true)))
            .ToImmutableArray();
        var group = fixture.Request.Scan.Groups[0] with
        {
            Members = members,
            Equality = ReviewFixture.ExactEvidence(members),
        };
        fixture.ReplaceRequest(fixture.Request with
        {
            Scan = fixture.Request.Scan with { Groups = [group], Occurrences = members, Counts = new(2, 2, 0, 0, 0, 0) },
        });
        await fixture.OpenAsync();
        await fixture.PumpAsync(fixture.Executor.Arrived.Task);
        await fixture.RevalidateAsync(1, 3);
        Assert.Equal(ReviewCommandOutcome.Committed, (await fixture.ChangeAsync(fixture.SelectionCommand(0,
            SelectionCommandKind.ReplaceFocusedGroup, 1, 3))).Outcome);
        var request = (await fixture.ApproveAsync(fixture.ApprovalCommand())).ApprovedRequest!;
        var claim = refresh ? (await ClaimAsync(fixture, request)).Claim : null;
        currentLinks = 1;
        if (refresh)
        {
            var pending = fixture.Owner.RefreshSafetyAsync(claim!, request.Items[0], [], TestContext.Current.CancellationToken);
            await fixture.PumpAsync(pending);
            Assert.Equal(ApprovedActionClaimOutcome.UnsafeCurrentFacts, (await pending).Outcome);
            Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, (await pending).Reason);
        }
        else
        {
            var result = await ClaimAsync(fixture, request);
            Assert.Equal(ApprovedActionClaimOutcome.UnsafeCurrentFacts, result.Outcome);
            Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, result.Reason);
        }
        Assert.Equal(refresh ? 4 : 2, reader.Leases.Count);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OpenedFinalLinkOrAllocationDisagreementBlocksAuthorityDespiteKnownWitness(bool refresh, bool allocation)
    {
        var reader = Reader();
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var request = await ApproveAsync(fixture);
        var claim = refresh ? (await ClaimAsync(fixture, request)).Claim : null;
        var normal = reader.Create!;
        reader.Create = (index, open) =>
        {
            var lease = normal(index, open);
            if (open.ExpectedPhysicalFileKey.FileId.Low == 1)
                lease.Refresh = _ => ValueTask.FromResult(lease.OpenedFacts with
                {
                    Allocation = allocation ? new(8192, 2, true) : new(4096, 3, true),
                });
            return lease;
        };
        if (refresh)
        {
            var pending = fixture.Owner.RefreshSafetyAsync(claim!, request.Items[0], [], TestContext.Current.CancellationToken);
            await fixture.PumpAsync(pending);
            Assert.Equal(ApprovedActionClaimOutcome.UnsafeCurrentFacts, (await pending).Outcome);
            Assert.Equal(ActionSafetyReason.InconsistentLinks, (await pending).Reason);
        }
        else
        {
            var result = await ClaimAsync(fixture, request);
            Assert.Equal(ApprovedActionClaimOutcome.UnsafeCurrentFacts, result.Outcome);
            Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
        }
        Assert.Equal(0, reader.Live);
    }

    private static async Task<ApprovedActionClaimOutcome> ClaimOutcomeAsync(ReviewCoordinator owner, ApprovedActionRequest request) =>
        (await owner.ClaimAsync(request, [], CancellationToken.None)).Outcome;

    private static async Task<ApprovedActionClaimOutcome> RefreshOutcomeAsync(ReviewCoordinator owner,
        ApprovedActionClaim claim, ApprovedActionItem item) =>
        (await owner.RefreshSafetyAsync(claim, item, [], CancellationToken.None)).Outcome;

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
    public async Task OverlappingSelectionRefreshesEveryAffectedGroup()
    {
        var reader = new ScanTestReader();
        var analyzer = new ScanTestAnalyzer();
        await using var fixture = new ApprovalFixture(new(),
            actionSafety: new(reader, analyzer));
        var request = ReviewCoordinatorTests.MixedRequest(fixture.Request,
            includeTerminalFailures: false);
        reader.Create = (_, open) =>
        {
            var member = request.Scan.Occurrences.Single(value =>
                value.PhysicalFileKey == open.ExpectedPhysicalFileKey);
            return new ScanTestLease(open)
            {
                Content = new MemoryStream([checked((byte)member.Token.OccurrenceId)]),
                OpenedFacts = new(open.ExpectedPhysicalFileKey, 1,
                    open.ResolvedPath, null, new(4096, 1, true)),
            };
        };
        analyzer.Hash = (content, _) =>
        {
            var occurrenceId = content.ReadByte();
            var member = request.Scan.Occurrences.Single(value =>
                value.Token.OccurrenceId == occurrenceId);
            return ValueTask.FromResult(new StreamDigest(
                member.Content!.ByteLength, member.Content.Sha256));
        };
        fixture.ReplaceRequest(request);
        Assert.Equal(ReviewCommandOutcome.Committed,
            (await fixture.OpenAsync()).Outcome);
        await fixture.PumpAsync(fixture.Executor.Arrived.Task);
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
        {
            var revalidation = new ReviewRevalidationRequest(fixture.Owner.Current.Pin!,
                new(request.Scan.Pin!, request.Scope.Preflight!.Scope!), group.GroupId,
                group.GroupRevision, member, 1);
            var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value,
                member.Content!.ByteLength, member.ResolvedPath, null,
                member.Allocation);
            var accepted = fixture.Owner.CommitRevalidationAsync(new(revalidation,
                facts, facts, new(member.Content.ByteLength,
                    member.Content.Sha256))).AsTask();
            await fixture.PumpAsync(accepted);
            Assert.True(await accepted);
        }
        Assert.Equal(ReviewCommandOutcome.Committed,
            (await fixture.ChangeAsync(fixture.SelectionCommand(0,
                SelectionCommandKind.Toggle, 1))).Outcome);
        var approved = (await fixture.ApproveAsync(fixture.ApprovalCommand()))
            .ApprovedRequest!;
        var claim = await ClaimAsync(fixture, approved);
        Assert.Equal(ApprovedActionClaimOutcome.Claimed, claim.Outcome);
        Assert.Equal(5, reader.Leases.Count);

        var refreshing = fixture.Owner.RefreshSafetyAsync(claim.Claim!,
            approved.Items[0], [], TestContext.Current.CancellationToken);
        await fixture.PumpAsync(refreshing);
        var refreshed = await refreshing;

        Assert.Equal(ApprovedActionClaimOutcome.Claimed, refreshed.Outcome);
        Assert.Equal(10, reader.Leases.Count);
        Assert.Equal(approved.Items[0].GroupId,
            refreshed.Envelope!.Input.ApprovedGroup.GroupId);
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
