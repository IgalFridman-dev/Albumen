using System.Collections.Immutable;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Review;

public sealed class ReviewApprovalAuthorityTests
{
    [Fact]
    public async Task FormerRequestCannotClaimAnUnsealedReplacementReview()
    {
        var reader = ApprovedActionAuthorityTests.Reader();
        await using var fixture = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var request = await ApprovedActionAuthorityTests.ApproveAsync(fixture);
        await fixture.OpenAsync();
        var result = await ApprovedActionAuthorityTests.ClaimAsync(fixture, request);
        Assert.Equal(ApprovedActionClaimOutcome.Stale, result.Outcome);
        Assert.Empty(reader.Leases);
        Assert.False(fixture.Owner.Current.Sealed);
    }

    [Fact]
    public async Task EmptySelectionCannotCreateAnActionRequest()
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        Assert.Equal(ReviewCommandOutcome.Committed, (await fixture.OpenAsync()).Outcome);

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Null(result.ApprovedRequest);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Fact]
    public async Task EmptyIdempotencyKeyCannotCreateAnActionRequest()
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        await fixture.OpenSelectedAsync();

        var result = await fixture.ApproveAsync(
            fixture.ApprovalCommand(idempotencyKey: Guid.Empty));

        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingOrUnavailableMembersCannotBeSelectedIntoApproval(bool pending)
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        Assert.Equal(ReviewCommandOutcome.Committed, (await fixture.OpenAsync()).Outcome);
        await fixture.PumpAsync(fixture.Executor.Arrived.Task);
        if (!pending)
        {
            await fixture.FailAsync(1, ReviewUnavailableReason.Missing);
        }

        var selection = await fixture.ChangeAsync(
            fixture.SelectionCommand(0, SelectionCommandKind.Toggle, 1));
        var approval = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.Invalid, selection.Outcome);
        Assert.Equal(ReviewCommandOutcome.Invalid, approval.Outcome);
        Assert.Null(approval.Snapshot.ApprovedRequest);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("selection")]
    public async Task EveryStaleApprovalPinRejectsTheWholeCommand(string mismatch)
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        await fixture.OpenSelectedAsync();
        var command = fixture.ApprovalCommand();
        var session = command.Session;
        command = mismatch switch
        {
            "review" => command with { Session = session with { ReviewSessionId = Guid.NewGuid() } },
            "scan" => command with { Session = session with { Scan = session.Scan with { ScanId = Guid.NewGuid() } } },
            "scope" => command with { Session = session with { Scan = session.Scan with { ScopeRevision = 99 } } },
            "rules" => command with { Session = session with { Scan = session.Scan with { RuleSetRevision = 99 } } },
            "analysis" => command with { Session = session with { Scan = session.Scan with { AnalysisRevision = 99 } } },
            _ => command with { SelectionRevision = 99 },
        };

        var result = await fixture.ApproveAsync(command);

        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Null(result.ApprovedRequest);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Theory]
    [InlineData(ScanStatus.Idle)]
    [InlineData(ScanStatus.Checking)]
    [InlineData(ScanStatus.Running)]
    [InlineData(ScanStatus.Faulted)]
    public async Task NonterminalOrFaultedScanNeverCreatesApprovalAuthority(ScanStatus status)
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        var opened = await fixture.OpenAsync(
            fixture.Request with { Scan = fixture.Request.Scan with { Status = status } });

        Assert.Equal(ReviewCommandOutcome.Invalid, opened.Outcome);
        Assert.Null(fixture.Owner.Current.ApprovedRequest);
        Assert.False(fixture.Owner.Current.Sealed);
    }

    [Fact]
    public async Task IncompleteCoverageWithoutAnyDisclosedUnresolvedFactIsRejected()
    {
        await using var fixture = new ApprovalFixture(
            new ApprovalEvidenceStore(), ScanStatus.Incomplete);
        var emptyIncomplete = fixture.Request with
        {
            Scan = fixture.Request.Scan with
            {
                Enumeration = ScanTestData.Success,
                Counts = new(5, 5, 0, 0, 0, 0),
            },
        };
        Assert.Equal(ReviewCommandOutcome.Committed,
            (await fixture.OpenAsync(emptyIncomplete)).Outcome);
        await fixture.PumpAsync(fixture.Executor.Arrived.Task);
        await fixture.RevalidateAsync(1, 2, 3, 4, 5);
        Assert.Equal(ReviewCommandOutcome.Committed,
            (await fixture.ChangeAsync(fixture.SelectionCommand(0,
                SelectionCommandKind.ReplaceFocusedGroup, 1, 2))).Outcome);

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Null(result.Snapshot.ApprovedRequest);
    }

    [Fact]
    public async Task UnrepresentableUnresolvedTotalIsRejectedBeforeApprovalIsSealed()
    {
        await using var fixture = new ApprovalFixture(
            new ApprovalEvidenceStore(), ScanStatus.Incomplete);
        fixture.ReplaceRequest(fixture.Request with
        {
            Scan = fixture.Request.Scan with
            {
                Enumeration = ScanTestData.Success,
                Counts = new(5, 5, 0, long.MaxValue, 1, 0),
            },
        });
        await fixture.OpenSelectedAsync();
        var selectionRevision = fixture.Owner.Current.SelectionRevision;

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Equal(selectionRevision, result.Snapshot.SelectionRevision);
        Assert.Null(result.ApprovedRequest);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Fact]
    public async Task FaultedReviewCannotApproveItsFormerSelection()
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        await fixture.OpenSelectedAsync();
        var command = fixture.ApprovalCommand();
        var faulted = fixture.Owner.ReportObserverFaultAsync(fixture.Owner.Current.Pin!);
        await fixture.PumpAsync(faulted);

        var result = await fixture.ApproveAsync(command);

        Assert.Equal(ReviewCommandOutcome.Faulted, result.Outcome);
        Assert.Null(result.Snapshot.ApprovedRequest);
    }

    [Theory]
    [InlineData(OverrideReadOutcome.Missing)]
    [InlineData(OverrideReadOutcome.Corrupt)]
    [InlineData(OverrideReadOutcome.Unavailable)]
    public async Task FailedOverrideEvidenceReadBlocksApproval(OverrideReadOutcome outcome)
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store);
        await fixture.OpenOverrideSelectedAsync();
        store.Read = (_, _) => ValueTask.FromResult(new OverrideReadResult(outcome, null));

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.EvidenceUnavailable, result.Outcome);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Fact]
    public async Task ThrowingOverrideEvidenceReadIsAClosedUnavailableResult()
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store);
        await fixture.OpenOverrideSelectedAsync();
        store.Read = (_, _) => ValueTask.FromException<OverrideReadResult>(
            new IOException("injected unreadable evidence"));

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.EvidenceUnavailable, result.Outcome);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("affirmed-utc")]
    [InlineData("review")]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("group")]
    [InlineData("group-revision")]
    [InlineData("selection")]
    [InlineData("occurrence")]
    [InlineData("affected")]
    [InlineData("policy")]
    [InlineData("consequence")]
    [InlineData("affirmation")]
    public async Task EveryMismatchedOrStaleOverrideEvidenceFactBlocksApproval(string mismatch)
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store);
        await fixture.OpenOverrideSelectedAsync();
        var exact = fixture.OverrideEvidence!;
        var pin = exact.Pin;
        var scan = pin.Session.Scan;
        var changed = mismatch switch
        {
            "evidence" => exact with { EvidenceId = new(Guid.NewGuid()) },
            "affirmed-utc" => exact with { AffirmedUtc = exact.AffirmedUtc.AddTicks(1) },
            "review" => exact with { Pin = pin with { Session = pin.Session with { ReviewSessionId = Guid.NewGuid() } } },
            "scan" => exact with { Pin = pin with { Session = pin.Session with { Scan = scan with { ScanId = Guid.NewGuid() } } } },
            "scope" => exact with { Pin = pin with { Session = pin.Session with { Scan = scan with { ScopeRevision = 99 } } } },
            "rules" => exact with { Pin = pin with { Session = pin.Session with { Scan = scan with { RuleSetRevision = 99 } } } },
            "analysis" => exact with { Pin = pin with { Session = pin.Session with { Scan = scan with { AnalysisRevision = 99 } } } },
            "group" => exact with { Pin = pin with { GroupId = Guid.NewGuid() } },
            "group-revision" => exact with { Pin = pin with { GroupRevision = 99 } },
            "selection" => exact with { Pin = pin with { SelectionRevision = 99 } },
            "occurrence" => exact with { SelectedOccurrenceIds = [1, 2] },
            "affected" => exact with { AffectedPhysicalRevisions = ChangeAffected(exact) },
            "policy" => exact with { SurvivorPolicyVersion = "survivor-v0" },
            "consequence" => exact with { ConsequenceTextVersion = "unversioned" },
            _ => exact with { DeliberatelyAffirmed = false },
        };
        store.Read = (_, _) => ValueTask.FromResult(
            new OverrideReadResult(OverrideReadOutcome.Found, changed));

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        Assert.Equal(ReviewCommandOutcome.EvidenceUnavailable, result.Outcome);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
    }

    [Fact]
    public async Task RetainedExactOverrideCanApproveAfterAnUnselectedSurvivorBecomesEligible()
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store);
        Assert.Equal(ReviewCommandOutcome.Committed, (await fixture.OpenAsync()).Outcome);
        await fixture.PumpAsync(fixture.Executor.Arrived.Task);
        await fixture.RevalidateAsync(1, 2);
        var proposal = await fixture.ChangeAsync(fixture.SelectionCommand(0,
            SelectionCommandKind.ReplaceFocusedGroup, 1, 2));
        Assert.Equal(ReviewCommandOutcome.OverrideRequired, proposal.Outcome);
        var evidenceId = new OverrideEvidenceId(
            Guid.Parse("0a70e6aa-0186-4654-9ed7-d3ae574f1552"));
        var affirmed = await fixture.AffirmAsync(new(proposal.OverrideProposal!, evidenceId,
            new DateTimeOffset(2026, 9, 9, 17, 30, 0, TimeSpan.Zero)));
        Assert.Equal(ReviewCommandOutcome.Committed, affirmed.Outcome);
        var selectionRevision = affirmed.Snapshot.SelectionRevision;

        await fixture.RevalidateAsync(3);
        var unchanged = await fixture.ChangeAsync(fixture.SelectionCommand(0,
            SelectionCommandKind.ReplaceFocusedGroup, 2, 1));

        Assert.Equal(ReviewCommandOutcome.Neutral, unchanged.Outcome);
        Assert.Equal(selectionRevision, unchanged.Snapshot.SelectionRevision);
        Assert.Equal(evidenceId, unchanged.Snapshot.Groups[0].OverrideEvidenceId);
        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());
        Assert.Equal(ReviewCommandOutcome.Approved, result.Outcome);
        Assert.True(result.Snapshot.Sealed);
        Assert.Same(result.ApprovedRequest, result.Snapshot.ApprovedRequest);
        Assert.Equal([evidenceId], store.ReadEvidenceIds);
        Assert.All(result.ApprovedRequest!.Items,
            item => Assert.Equal(evidenceId, item.OverrideEvidenceId));
    }

    [Theory]
    [InlineData(ReviewOwnerEvent.ReviewOpen)]
    [InlineData(ReviewOwnerEvent.Selection)]
    [InlineData(ReviewOwnerEvent.OverrideDialogOpen)]
    public async Task NonApprovalOwnerEventsNeverCreateAnActionRequest(ReviewOwnerEvent action)
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store);
        await fixture.OpenSelectedAsync();

        await ApplyAsync(fixture, action);

        Assert.Null(fixture.Owner.Current.ApprovedRequest);
        Assert.False(fixture.Owner.Current.Sealed);
        Assert.Empty(store.ReadEvidenceIds);
    }

    private static ImmutableArray<AffectedPhysicalRevision> ChangeAffected(
        DeliberateOverrideEvidence evidence)
    {
        var first = evidence.AffectedPhysicalRevisions[0];
        return evidence.AffectedPhysicalRevisions.SetItem(0,
            first with { Allocation = first.Allocation with { LinkCount = 99 } });
    }

    private static async Task ApplyAsync(ApprovalFixture fixture, ReviewOwnerEvent action)
    {
        switch (action)
        {
            case ReviewOwnerEvent.ReviewOpen:
                var opened = fixture.Owner.OpenAsync(fixture.Request,
                    TestContext.Current.CancellationToken);
                await fixture.PumpAsync(opened);
                await opened;
                break;
            case ReviewOwnerEvent.Selection:
                await fixture.ChangeAsync(
                    fixture.SelectionCommand(0, SelectionCommandKind.Toggle, 3));
                break;
            case ReviewOwnerEvent.OverrideDialogOpen:
                await fixture.ChangeAsync(fixture.SelectionCommand(0,
                    SelectionCommandKind.ReplaceFocusedGroup, 1, 2, 3));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }
}

public enum ReviewOwnerEvent
{
    ReviewOpen,
    Selection,
    OverrideDialogOpen,
}
