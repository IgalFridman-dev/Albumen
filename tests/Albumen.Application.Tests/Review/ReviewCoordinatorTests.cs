using System.Collections.Concurrent;
using System.Collections.Immutable;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Shell;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Xunit;

namespace Albumen.Application.Tests.Review;

public sealed class ReviewCoordinatorTests
{
    [Fact]
    public async Task InvalidConfirmedOptionsCannotOpenReview()
    {
        await using var f = new ReviewFixture();
        var request = f.Request with
        {
            Rules = f.Request.Rules with
            {
                Options = new(ExactDuplicateMode.Disabled, NearDuplicateMode.Conservative),
            },
        };

        var opened = await f.Open(request);

        Assert.Equal(ReviewCommandOutcome.Invalid, opened.Outcome);
        Assert.Null(f.Owner.Current.Pin);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task MalformedOptionsOnSameScanCloseRevokeAndCancelPriorReviewAuthority()
    {
        await using var f = new ReviewFixture();
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open()).Outcome);
        var run = await f.Run();
        var cancellationRequested = ScanTestData.Signal<bool>();
        run.OnCancel = () =>
        {
            cancellationRequested.TrySetResult(true);
            return Task.CompletedTask;
        };
        var staleCommand = f.Command(SelectionCommandKind.Toggle, 1);
        var malformed = f.Request with
        {
            Rules = f.Request.Rules with { Options = null! },
        };

        var opened = await f.Open(malformed);
        await f.Pump(cancellationRequested.Task);
        await f.Pump(run.Disposed.Task);
        var late = await f.Change(staleCommand);
        var reopened = await f.Open();

        Assert.Equal(ReviewCommandOutcome.Invalid, opened.Outcome);
        Assert.Equal(ReviewStatus.Closed, opened.Snapshot.Status);
        Assert.Null(f.Owner.Current.Pin);
        Assert.Equal(ReviewCommandOutcome.Stale, late.Outcome);
        Assert.Equal(ReviewCommandOutcome.Stale, reopened.Outcome);
        Assert.Single(f.Executor.Runs);
    }

    [Theory]
    [InlineData(CandidateCategory.NearDuplicate)]
    [InlineData(CandidateCategory.Uncertain)]
    public async Task ExactOnlyRulesRejectNearCategoryEvidence(CandidateCategory category)
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        var group = request.Scan.Groups.Single(candidate => candidate.Category == category);
        request = request with
        {
            Rules = request.Rules with
            {
                Options = new(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled),
            },
            Scan = request.Scan with { Groups = [group] },
        };

        var opened = await f.Open(request);

        Assert.Equal(ReviewCommandOutcome.Invalid, opened.Outcome);
        Assert.Null(f.Owner.Current.Pin);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task CompleteConservativeNearScanWithTerminalFailuresOpensMixedReview()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: true);

        var opened = await f.Open(request);

        Assert.Equal(ReviewCommandOutcome.Committed, opened.Outcome);
        Assert.Equal(3, opened.Snapshot.Groups.Length);
        Assert.Equal(2, request.Scan.Counts.Failed);
        Assert.Equal(2, request.Scan.Counts.Unsupported);
        var unsupported = request.Scan.Occurrences.Skip(5).Take(2).ToArray();
        Assert.All(unsupported, row =>
        {
            Assert.Equal(ScanStageState.Unsupported, row.Enumerate.State);
            Assert.Equal(ScanStageState.NotApplicable, row.Exact.State);
            Assert.Null(row.Content);
        });
        var failed = request.Scan.Occurrences.Skip(7).Take(2).ToArray();
        Assert.All(failed, row =>
        {
            Assert.Equal(ScanStageState.Succeeded, row.Enumerate.State);
            Assert.Equal(ScanStageState.Succeeded, row.Exact.State);
            Assert.NotNull(row.Content);
        });
        Assert.Contains(failed, row => row.VisualFeatures.State == ScanStageState.FailedRetryable);
        Assert.Contains(failed, row => row.NearDuplicate.State == ScanStageState.FailedRetryable);
        await f.Run();
    }

    [Fact]
    public async Task LookAlikeSetWithNearAndUncertainEvidenceOpensReview()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        var occurrences = request.Scan.Occurrences;
        // Three Near photos plus one photo that is Uncertain to all three form one
        // Uncertain set whose evidence keeps both kinds of edges.
        var set = new CandidateGroup(Guid.NewGuid(), 1, request.Scan.Pin!, CandidateCategory.Uncertain,
            [occurrences[1], occurrences[2], occurrences[3], occurrences[4]], [], true, null,
            new(null, RecoveryUnavailableReason.InconsistentFacts))
        {
            Similarity =
            [
                new(occurrences[1].Token, occurrences[2].Token, 1, 2, NearRelationshipKind.Near),
                new(occurrences[1].Token, occurrences[4].Token, 2, 3, NearRelationshipKind.Near),
                new(occurrences[2].Token, occurrences[4].Token, 1, 1, NearRelationshipKind.Near),
                new(occurrences[1].Token, occurrences[3].Token, 8, 20, NearRelationshipKind.Uncertain),
                new(occurrences[2].Token, occurrences[3].Token, 9, 18, NearRelationshipKind.Uncertain),
                new(occurrences[3].Token, occurrences[4].Token, 7, 22, NearRelationshipKind.Uncertain),
            ],
        };
        request = request with
        {
            Scan = request.Scan with { Groups = [request.Scan.Groups[0], set] },
        };

        var opened = await f.Open(request);

        Assert.Equal(ReviewCommandOutcome.Committed, opened.Outcome);
        Assert.Contains(opened.Snapshot.Groups, group => group.Candidate.GroupId == set.GroupId);
        await f.Run();
    }

    [Theory]
    [InlineData("only-near")]
    [InlineData("near-edge-too-far")]
    public async Task UncertainSetStillRejectsEvidenceThatDoesNotMatchItsKind(string mismatch)
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        var occurrences = request.Scan.Occurrences;
        ImmutableArray<NearDuplicateEvidence> similarity = mismatch == "only-near"
            ? [new(occurrences[2].Token, occurrences[3].Token, 1, 2, NearRelationshipKind.Near)]
            :
            [
                new(occurrences[2].Token, occurrences[3].Token, 8, 20, NearRelationshipKind.Near),
            ];
        var groups = request.Scan.Groups.SetItem(2, request.Scan.Groups[2] with { Similarity = similarity });
        request = request with { Scan = request.Scan with { Groups = groups } };

        var opened = await f.Open(request);

        Assert.Equal(ReviewCommandOutcome.Invalid, opened.Outcome);
        Assert.Empty(f.Executor.Runs);
    }

    [Theory]
    [InlineData("foreign-endpoint")]
    [InlineData("wrong-kind")]
    [InlineData("equality-content")]
    public async Task MixedReviewRejectsMalformedCategoryEvidenceBeforeExecutorStart(string mismatch)
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        var groups = request.Scan.Groups;
        if (mismatch == "foreign-endpoint")
        {
            var near = groups[1];
            var edge = near.Similarity[0] with { Left = request.Scan.Occurrences[4].Token };
            groups = groups.SetItem(1, near with { Similarity = near.Similarity.SetItem(0, edge) });
        }
        else if (mismatch == "wrong-kind")
        {
            var near = groups[1];
            groups = groups.SetItem(1, near with
            {
                Similarity = near.Similarity.SetItem(0,
                    near.Similarity[0] with { Kind = NearRelationshipKind.Uncertain }),
            });
        }
        else
        {
            var exact = groups[0];
            groups = groups.SetItem(0, exact with
            {
                Equality = exact.Equality.SetItem(0,
                    exact.Equality[0] with { LeftContent = request.Scan.Occurrences[4].Content! }),
            });
        }
        request = request with { Scan = request.Scan with { Groups = groups } };

        var opened = await f.Open(request);

        Assert.Equal(ReviewCommandOutcome.Invalid, opened.Outcome);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task OverlappingSelectionUsesGlobalSurvivorsAndApprovalDeduplicatesOccurrences()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open(request)).Outcome);
        await f.Run();
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
            await CommitEvidence(f, request, group, member);

        var first = await Change(f, 0, SelectionCommandKind.Toggle, 1);
        var duplicate = await Change(f, 1, SelectionCommandKind.Toggle, 1);
        var hiddenFinalSurvivor = await Change(f, 1,
            SelectionCommandKind.ReplaceFocusedGroup, 2, 3);
        var second = await Change(f, 2, SelectionCommandKind.Toggle, 4);
        var snapshot = f.Owner.Current;
        var approval = f.Owner.ApproveAsync(new(snapshot.Pin!, snapshot.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await f.Pump(approval);
        var approved = await approval;

        Assert.Equal(ReviewCommandOutcome.Committed, first.Outcome);
        Assert.Equal(ReviewCommandOutcome.Invalid, duplicate.Outcome);
        Assert.Equal(ReviewCommandOutcome.Invalid, hiddenFinalSurvivor.Outcome);
        Assert.Equal(ReviewCommandOutcome.Committed, second.Outcome);
        Assert.Equal(ReviewCommandOutcome.Approved, approved.Outcome);
        Assert.Equal([1L, 4L], approved.ApprovedRequest!.SelectedOccurrenceIds);
        Assert.Equal(2, approved.ApprovedRequest.Items.Length);
        Assert.Equal(2, approved.ApprovedRequest.Disclosure.SelectedOccurrenceCount);
        Assert.Equal(2, approved.ApprovedRequest.Disclosure.Unresolved.UnselectedSurvivorMembers);
        Assert.Equal(8192, approved.ApprovedRequest.Disclosure.Recovery.Bytes);
    }

    [Fact]
    public async Task AMarkHeldInOneSetIsProjectedOntoTheSameOccurrenceInEveryOtherSet()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open(request)).Outcome);
        await f.Run();
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
            await CommitEvidence(f, request, group, member);
        var exactGroupId = f.Owner.Current.Groups[0].Candidate.GroupId;

        Assert.Equal(ReviewCommandOutcome.Committed,
            (await Change(f, 0, SelectionCommandKind.Toggle, 1)).Outcome);
        var marked = f.Owner.Current;
        Assert.True(Member(marked, 0, 1).Selected);
        Assert.Null(Member(marked, 0, 1).SelectedInGroup);
        Assert.False(Member(marked, 1, 1).Selected);
        Assert.Equal(exactGroupId, Member(marked, 1, 1).SelectedInGroup);
        Assert.Null(Member(marked, 1, 2).SelectedInGroup);
        Assert.Empty(marked.Groups[1].SelectedOccurrenceIds);

        Assert.Equal(ReviewCommandOutcome.Committed,
            (await Change(f, 0, SelectionCommandKind.Toggle, 1)).Outcome);
        var unmarked = f.Owner.Current;
        Assert.False(Member(unmarked, 0, 1).Selected);
        Assert.Null(Member(unmarked, 1, 1).SelectedInGroup);
    }

    [Fact]
    public async Task AMarkIsRefusedWhileTheSamePhotoIsStillBeingCheckedInAnotherSetSoNothingIsProjected()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open(request)).Outcome);
        await f.Run();
        var near = request.Scan.Groups[1];
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
        {
            if (group == near && member.Token.OccurrenceId == 1) continue;
            await CommitEvidence(f, request, group, member);
        }

        var refused = await Change(f, 0, SelectionCommandKind.Toggle, 1);

        Assert.Equal(ReviewCommandOutcome.Invalid, refused.Outcome);
        Assert.Equal(ReviewEligibilityState.PendingRevalidation, Member(f.Owner.Current, 1, 1).Eligibility);
        Assert.False(Member(f.Owner.Current, 0, 1).Selected);
        Assert.Null(Member(f.Owner.Current, 1, 1).SelectedInGroup);
        Assert.Empty(f.Owner.Current.Groups[0].SelectedOccurrenceIds);
    }

    // PM-013: after a cleanup the user keeps reviewing the same scan, without the
    // photos that reached the Recycle Bin and with no marks carried over.
    [Fact]
    public async Task AContinuationAfterTheCleanupOpensANewSessionWithoutTheRemovedPhotos()
    {
        await using var f = new ReviewFixture();
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open()).Outcome);
        await f.Eligible(1, 2, 3);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Change(f.Command(SelectionCommandKind.Toggle, 1))).Outcome);
        var approved = await Approve(f);
        var sealedSession = f.Owner.Current.Pin!;
        var firstRun = Assert.Single(f.Executor.Runs);
        f.Executor.Arrived = ScanTestData.Signal<ReviewTestRun>();

        var continued = await f.Open(Continuation(f.Request, approved.RequestId, excluded: [1]));

        Assert.Equal(ReviewCommandOutcome.Committed, continued.Outcome);
        var session = continued.Snapshot.Pin!;
        Assert.NotEqual(sealedSession.ReviewSessionId, session.ReviewSessionId);
        Assert.Equal(sealedSession.Scan, session.Scan);
        Assert.False(continued.Snapshot.Sealed);
        Assert.Null(continued.Snapshot.ApprovedRequest);
        var group = Assert.Single(continued.Snapshot.Groups);
        Assert.Same(f.Request.Scan.Groups[0], group.Candidate);
        Assert.False(group.Closed);
        Assert.Equal([2L, 3L], group.Members.Select(member => member.Occurrence.Token.OccurrenceId));
        Assert.All(group.Members, member => Assert.Equal(ReviewEligibilityState.PendingRevalidation, member.Eligibility));
        Assert.Empty(group.SelectedOccurrenceIds);
        var run = await f.Run();
        Assert.NotSame(firstRun, run);
        Assert.Equal([2L, 3L], Assert.Single(run.Groups).Members.Select(member => member.Token.OccurrenceId));
        await f.Pump(firstRun.Disposed.Task);
        await f.Eligible(2, 3);
        Assert.Equal(ReviewStatus.Ready, f.Owner.Current.Status);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Change(f.Command(SelectionCommandKind.Toggle, 2))).Outcome);
        var again = await Approve(f);
        Assert.NotEqual(approved.RequestId, again.RequestId);
        Assert.Equal(session.ReviewSessionId, again.ReviewSessionId);
        Assert.Equal([2L], again.SelectedOccurrenceIds);
    }

    [Theory]
    [InlineData("other-cleanup")]
    [InlineData("not-sealed")]
    public async Task AContinuationThatDoesNotMatchTheSealedReviewIsStale(string mismatch)
    {
        await using var f = new ReviewFixture();
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open()).Outcome);
        await f.Eligible(1, 2, 3);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Change(f.Command(SelectionCommandKind.Toggle, 1))).Outcome);
        if (mismatch == "other-cleanup") await Approve(f);
        var before = f.Owner.Current;

        var continued = await f.Open(Continuation(f.Request, Guid.NewGuid(), excluded: [1]));

        Assert.Equal(ReviewCommandOutcome.Stale, continued.Outcome);
        Assert.Same(before, f.Owner.Current);
        Assert.Single(f.Executor.Runs);
    }

    [Fact]
    public async Task ASetLeftWithOnePhotoOrAnUncertainOutcomeIsClosedUntilTheNextScan()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open(request)).Outcome);
        await f.Run();
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
            await CommitEvidence(f, request, group, member);
        Assert.Equal(ReviewCommandOutcome.Committed, (await Change(f, 0, SelectionCommandKind.Toggle, 1)).Outcome);
        var approved = await Approve(f);
        var near = request.Scan.Groups[1];
        var uncertain = request.Scan.Groups[2];
        f.Executor.Arrived = ScanTestData.Signal<ReviewTestRun>();

        var continued = await f.Open(Continuation(request, approved.RequestId, excluded: [1], closed: [uncertain.GroupId]));

        Assert.Equal(ReviewCommandOutcome.Committed, continued.Outcome);
        var snapshot = continued.Snapshot;
        Assert.Equal(request.Scan.Groups.ToArray(), snapshot.Groups.Select(group => group.Candidate).ToArray());
        // The identical-copies set lost one of its two photos, so there is nothing left to compare.
        Assert.True(snapshot.Groups[0].Closed);
        Assert.Equal([2L], snapshot.Groups[0].Members.Select(member => member.Occurrence.Token.OccurrenceId));
        Assert.False(snapshot.Groups[1].Closed);
        Assert.Equal([2L, 3L], snapshot.Groups[1].Members.Select(member => member.Occurrence.Token.OccurrenceId));
        Assert.True(snapshot.Groups[2].Closed);
        Assert.Equal([3L, 4L], snapshot.Groups[2].Members.Select(member => member.Occurrence.Token.OccurrenceId));
        Assert.All(snapshot.Groups.Where(group => group.Closed).SelectMany(group => group.Members),
            member => Assert.Equal(ReviewEligibilityState.Unavailable, member.Eligibility));
        Assert.Equal(2, snapshot.Unresolved.PendingReviewMembers);
        Assert.Equal(0, snapshot.Unresolved.UnavailableReviewMembers);
        var run = await f.Run();
        Assert.Equal([near.GroupId], run.Groups.Select(group => group.GroupId));
        Assert.Equal([2L, 3L], run.Groups[0].Members.Select(member => member.Token.OccurrenceId));
        foreach (var member in near.Members.Where(member => member.Token.OccurrenceId != 1))
            await CommitEvidence(f, request, near, member);
        Assert.Equal(ReviewStatus.Ready, f.Owner.Current.Status);

        Assert.Equal(ReviewCommandOutcome.Invalid, (await Change(f, 0, SelectionCommandKind.Toggle, 2)).Outcome);
        Assert.Equal(ReviewCommandOutcome.Invalid, (await Change(f, 2, SelectionCommandKind.Toggle, 3)).Outcome);
        Assert.Equal(ReviewCommandOutcome.Committed, (await Change(f, 1, SelectionCommandKind.Toggle, 2)).Outcome);
        var again = await Approve(f);
        Assert.Equal([2L], again.SelectedOccurrenceIds);
        Assert.Equal(near.GroupId, Assert.Single(again.Items).GroupId);
    }

    [Theory]
    [InlineData("unknown-photo")]
    [InlineData("unknown-set")]
    [InlineData("no-cleanup")]
    public async Task AMalformedContinuationIsInvalid(string mismatch)
    {
        await using var f = new ReviewFixture();
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open()).Outcome);
        await f.Eligible(1, 2, 3);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Change(f.Command(SelectionCommandKind.Toggle, 1))).Outcome);
        var approved = await Approve(f);
        var request = mismatch switch
        {
            "unknown-photo" => Continuation(f.Request, approved.RequestId, excluded: [1, 99]),
            "unknown-set" => Continuation(f.Request, approved.RequestId, excluded: [1], closed: [Guid.NewGuid()]),
            _ => Continuation(f.Request, approved.RequestId, excluded: [1]) with { ContinuesRequestId = null },
        };

        Assert.Equal(ReviewCommandOutcome.Invalid, (await f.Open(request)).Outcome);
        Assert.Equal(ReviewStatus.Closed, f.Owner.Current.Status);
        Assert.Single(f.Executor.Runs);
    }

    [Fact]
    public async Task CompleteCoverageMayApproveWhileTerminalNearFailuresRemainDisclosed()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: true);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open(request)).Outcome);
        await f.Run();
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
            await CommitEvidence(f, request, group, member);
        Assert.Equal(ReviewCommandOutcome.Committed,
            (await Change(f, 0, SelectionCommandKind.Toggle, 1)).Outcome);
        var snapshot = f.Owner.Current;
        var approval = f.Owner.ApproveAsync(new(snapshot.Pin!, snapshot.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await f.Pump(approval);

        var approved = await approval;

        Assert.Equal(ReviewCommandOutcome.Approved, approved.Outcome);
        Assert.Equal(2, approved.ApprovedRequest!.Disclosure.Unresolved.FailedScanRows);
        Assert.Equal(ScanStatus.Complete, approved.ApprovedRequest.Disclosure.Coverage);
    }

    [Fact]
    public async Task ApprovalRejectsConflictingRevalidationFactsForAnOverlappingOccurrence()
    {
        await using var f = new ReviewFixture();
        var request = MixedRequest(f.Request, includeTerminalFailures: false);
        Assert.Equal(ReviewCommandOutcome.Committed, (await f.Open(request)).Outcome);
        await f.Run();
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
        {
            var allocation = group == request.Scan.Groups[1]
                && member.Token.OccurrenceId == 1
                    ? new AllocationEvidence(8192, 1, true)
                    : member.Allocation;
            await CommitEvidence(f, request, group, member, allocation);
        }
        Assert.Equal(ReviewCommandOutcome.Committed,
            (await Change(f, 0, SelectionCommandKind.Toggle, 1)).Outcome);
        var snapshot = f.Owner.Current;

        var approval = f.Owner.ApproveAsync(new(snapshot.Pin!, snapshot.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await f.Pump(approval);

        Assert.Equal(ReviewCommandOutcome.Invalid, (await approval).Outcome);
        Assert.False(f.Owner.Current.Sealed);
        Assert.Null(f.Owner.Current.ApprovedRequest);
    }

    [Fact]
    public async Task UnchangedAffirmedOverrideDoesNotBlockSelectionInAnotherGroup()
    {
        await using var f = new ReviewFixture();
        var pin = f.Request.Scan.Pin!;
        var context = new ScanRunContext(pin, f.Request.Scope.Preflight!.Scope!);
        var additional = Enumerable.Range(4, 2)
            .Select(id => ReviewData.Request(id, context).Occurrence)
            .ToImmutableArray();
        var second = new CandidateGroup(Guid.NewGuid(), 1, pin, CandidateCategory.Exact,
            additional, ReviewFixture.ExactEvidence(additional), false, null,
            new(4096, RecoveryUnavailableReason.None));
        var request = f.Request with
        {
            Scan = f.Request.Scan with
            {
                Occurrences = [.. f.Request.Scan.Occurrences, .. additional],
                Groups = [f.Request.Scan.Groups[0], second],
                Counts = new(5, 5, 0, 0, 0, 0),
            },
        };
        var session = new ReviewSessionPin(Guid.NewGuid(), pin);
        var store = new ReviewStore(session, request);
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
            Assert.True(store.Accept(RevalidationEvidence(session, request, group, member)));
        var first = store.Current.Groups[0].Candidate;
        var proposalResult = store.ChangeSelection(new(new(session, first.GroupId,
            first.GroupRevision, store.Current.SelectionRevision),
            SelectionCommandKind.ReplaceFocusedGroup, [1, 2, 3], Guid.NewGuid()));
        var proposal = Assert.IsType<OverrideProposalToken>(proposalResult.OverrideProposal);
        var started = store.BeginAffirmation(new(proposal, new(Guid.NewGuid()),
            DateTimeOffset.UtcNow));
        var committed = store.CompleteAffirmation(Assert.IsType<OverridePersistenceAttempt>(started.Attempt),
            new(OverridePersistOutcome.Persisted));
        Assert.Equal(ReviewCommandOutcome.Committed, committed.Outcome);

        var result = store.ChangeSelection(new(new(session, second.GroupId,
            second.GroupRevision, store.Current.SelectionRevision),
            SelectionCommandKind.Toggle, [4], Guid.NewGuid()));

        Assert.Equal(ReviewCommandOutcome.Committed, result.Outcome);
        Assert.Equal([1L, 2L, 3L], result.Snapshot.Groups[0].SelectedOccurrenceIds);
        Assert.NotNull(result.Snapshot.Groups[0].OverrideEvidenceId);
        Assert.Equal([4L], result.Snapshot.Groups[1].SelectedOccurrenceIds);
    }

    [Fact]
    public async Task DataSealPreventsMutationAndPreservesTheImmutableHandoff()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var session = f.Owner.Current.Pin!;
        var store = new ReviewStore(session, f.Request);
        Assert.True(store.Accept(f.Evidence(1)));
        Assert.True(store.Accept(f.Evidence(2)));
        Assert.True(store.Accept(f.Evidence(3)));
        var selected = store.ChangeSelection(f.Command(SelectionCommandKind.Toggle, 1));
        Assert.Equal(ReviewCommandOutcome.Committed, selected.Outcome);
        var member = f.Request.Scan.Groups[0].Members[0];
        var item = new ApprovedActionItem(f.Request.Scan.Groups[0].GroupId, 1, member.Token,
            member.DiscoveredPath, member.ResolvedPath, member.PhysicalFileKey!.Value, member.Content!, member.Allocation, null);
        var request = new ApprovedActionRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch, session.ReviewSessionId,
            3, session.Scan, f.Request.Scope.Preflight!.Scope!, [item], [1],
            new(1, 1, ScanStatus.Complete, selected.Snapshot.Unresolved, new(4096, SelectionRecoveryUnavailableReason.None),
                "Windows Recycle Bin", "Revalidate again"));
        store.Seal(request);
        var command = f.Command(SelectionCommandKind.Toggle, 2) with
        { Pin = f.Command(SelectionCommandKind.Toggle, 2).Pin with { SelectionRevision = 3 } };
        var result = store.ChangeSelection(command);
        Assert.Equal(ReviewCommandOutcome.Sealed, result.Outcome);
        Assert.Same(request, result.Snapshot.ApprovedRequest);
        Assert.Equal(3, result.Snapshot.SelectionRevision);
        Assert.Equal([1L], result.Snapshot.Groups[0].SelectedOccurrenceIds);
        Assert.True(result.Snapshot.Sealed);
        Assert.True(result.Snapshot.Groups[0].Sealed);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("not-ready")]
    [InlineData("selection")]
    [InlineData("group-pin")]
    [InlineData("row-token")]
    public async Task OpenRejectsInconsistentScopeRulesAndCandidatePinsBeforeExecutorStart(string mismatch)
    {
        await using var f = new ReviewFixture();
        var r = f.Request;
        var group = r.Scan.Groups[0];
        r = mismatch switch
        {
            "scope" => r with { Scope = r.Scope with { Revision = 2 } },
            "rules" => r with { Rules = new(1, 2) },
            "not-ready" => r with { Scope = r.Scope with { ScopeState = ScopeSessionState.Checking } },
            "selection" => r with { Scope = r.Scope with { Selection = new(@"C:\Other", ScopeRecursion.TopLevelOnly) } },
            "group-pin" => r with { Scan = r.Scan with { Groups = [group with { Pin = group.Pin with { AnalysisRevision = 2 } }] } },
            _ => r with { Scan = r.Scan with { Groups = [group with { Members = group.Members.SetItem(0,
                ReviewFixture.Copy(group.Members[0], token: group.Members[0].Token with { OccurrenceRevision = 2 })) }] } },
        };
        Assert.Equal(ReviewCommandOutcome.Invalid, (await f.Open(r)).Outcome);
        Assert.Empty(f.Executor.Runs);
        Assert.Null(f.Owner.Current.Pin);
    }

    [Theory]
    [InlineData("opened-key")]
    [InlineData("final-length")]
    [InlineData("final-path")]
    [InlineData("digest")]
    public async Task MalformedProtectedEvidenceFailsClosedForThePhysicalKey(string mismatch)
    {
        await using var f = new ReviewFixture(aliases: true);
        await f.Open();
        var e = f.Evidence(1);
        e = mismatch switch
        {
            "opened-key" => e with { Opened = e.Opened with { PhysicalFileKey = new(8, new(8, 8)) } },
            "final-length" => e with { Final = e.Final with { ByteLength = 2 } },
            "final-path" => e with { Final = e.Final with { ResolvedPath = @"C:\Other\1.jpg" } },
            _ => e with { Digest = new(1, new string('B', 64)) },
        };
        var ack = f.Owner.CommitRevalidationAsync(e).AsTask();
        await f.Pump(ack);
        Assert.True(await ack);
        Assert.All(f.Owner.Current.Groups[0].Members.Take(2), m => Assert.Equal(ReviewEligibilityState.Unavailable, m.Eligibility));
        Assert.Equal(ReviewEligibilityState.PendingRevalidation, f.Owner.Current.Groups[0].Members[2].Eligibility);
    }

    [Theory]
    [InlineData(ScanStatus.Complete)]
    [InlineData(ScanStatus.Incomplete)]
    public async Task TerminalReviewStartsPendingAndEmptyWithoutCreatingAuthority(ScanStatus coverage)
    {
        await using var f = new ReviewFixture();
        var request = f.Request with { Scan = f.Request.Scan with { Status = coverage } };
        var result = await f.Open(request);
        Assert.Equal(ReviewCommandOutcome.Committed, result.Outcome);
        Assert.Equal(coverage, result.Snapshot.Coverage);
        Assert.Equal(1, result.Snapshot.SelectionRevision);
        Assert.Equal(3, result.Snapshot.Unresolved.PendingReviewMembers);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
        Assert.All(result.Snapshot.Groups, g =>
        {
            Assert.Empty(g.SelectedOccurrenceIds);
            Assert.All(g.Members, m => Assert.Equal(ReviewEligibilityState.PendingRevalidation, m.Eligibility));
        });
        await f.Run();
    }

    [Theory]
    [InlineData(ScanStatus.Idle)]
    [InlineData(ScanStatus.Checking)]
    [InlineData(ScanStatus.Running)]
    [InlineData(ScanStatus.Faulted)]
    public async Task NonterminalOrFaultedScanCannotStartRevalidation(ScanStatus status)
    {
        await using var f = new ReviewFixture();
        var result = await f.Open(f.Request with { Scan = f.Request.Scan with { Status = status } });
        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Null(f.Owner.Current.Pin);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task PendingBulkIsAtomicAndEligibleSelectionAdvancesOnceWhileNeutralRetainsSnapshot()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var pending = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 1, 2));
        Assert.Equal(ReviewCommandOutcome.Invalid, pending.Outcome);
        Assert.Equal(1, f.Owner.Current.SelectionRevision);
        await f.Eligible(1, 2, 3);
        var prior = f.Owner.Current;
        var selected = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 1, 2));
        Assert.Equal(ReviewCommandOutcome.Committed, selected.Outcome);
        Assert.Equal(2, selected.Snapshot.SelectionRevision);
        Assert.Equal([1L, 2L], Assert.Single(selected.Snapshot.Groups).SelectedOccurrenceIds.Order());
        Assert.Equal(8192, Assert.Single(selected.Snapshot.Groups).Recovery.Bytes);
        Assert.Empty(Assert.Single(prior.Groups).SelectedOccurrenceIds);
        var neutral = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 2, 1));
        Assert.Equal(ReviewCommandOutcome.Neutral, neutral.Outcome);
        Assert.Same(selected.Snapshot, neutral.Snapshot);
        var toggle = await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        Assert.Equal(3, toggle.Snapshot.SelectionRevision);
        Assert.Equal([2L], Assert.Single(toggle.Snapshot.Groups).SelectedOccurrenceIds);
    }

    [Fact]
    public async Task LastSurvivorReturnsCanonicalProposalWithoutAnyMutationOrPersistence()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Eligible(1, 2, 3);
        var before = f.Owner.Current;
        var result = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 3, 1, 2));
        Assert.Equal(ReviewCommandOutcome.OverrideRequired, result.Outcome);
        Assert.Same(before, result.Snapshot);
        Assert.Equal([1L, 2L, 3L], result.OverrideProposal!.ProposedSelectedOccurrenceIds);
        Assert.Equal(before.SelectionRevision, result.OverrideProposal.Pin.SelectionRevision);
        Assert.Equal(SurvivorPolicy.Version, result.OverrideProposal.SurvivorPolicyVersion);
        Assert.Null(result.ApprovedRequest);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("group")]
    [InlineData("group-revision")]
    [InlineData("selection")]
    [InlineData("occurrence")]
    public async Task EntireStaleSelectionRejectsWithoutPartialCommit(string mismatch)
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Eligible(1, 2, 3);
        var command = f.Command(SelectionCommandKind.ReplaceFocusedGroup, 1, 2);
        var pin = command.Pin;
        command = mismatch switch
        {
            "review" => command with { Pin = pin with { Session = pin.Session with { ReviewSessionId = Guid.NewGuid() } } },
            "scan" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { ScanId = Guid.NewGuid() } } } },
            "scope" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { ScopeRevision = 2 } } } },
            "rules" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { RuleSetRevision = 2 } } } },
            "analysis" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { AnalysisRevision = 2 } } } },
            "group" => command with { Pin = pin with { GroupId = Guid.NewGuid() } },
            "group-revision" => command with { Pin = pin with { GroupRevision = 99 } },
            "selection" => command with { Pin = pin with { SelectionRevision = 99 } },
            _ => command with { OccurrenceIds = [1, 999] },
        };
        var before = f.Owner.Current;
        var result = await f.Change(command);
        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Same(before, result.Snapshot);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("occurrence-revision")]
    [InlineData("occurrence-attempt")]
    [InlineData("group-revision")]
    [InlineData("context")]
    [InlineData("content")]
    public async Task StaleEvidenceCannotChangeEligibility(string mismatch)
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var evidence = f.Evidence(1);
        var r = evidence.Request;
        r = mismatch switch
        {
            "attempt" => r with { Attempt = 2 },
            "occurrence-revision" => r with { Occurrence = ReviewFixture.Copy(r.Occurrence, token: r.Occurrence.Token with { OccurrenceRevision = 2 }) },
            "occurrence-attempt" => r with { Occurrence = ReviewFixture.Copy(r.Occurrence, token: r.Occurrence.Token with { Attempt = 2 }) },
            "group-revision" => r with { GroupRevision = 2 },
            "context" => r with { Context = r.Context with { Scope = r.Context.Scope with { RootIdentity = new(9, 9, 9) } } },
            _ => r with { Occurrence = ReviewFixture.Copy(r.Occurrence, content: new(r.Occurrence.PhysicalFileKey!.Value, 2, ScanTestData.Digest)) },
        };
        var before = f.Owner.Current;
        var ack = f.Owner.CommitRevalidationAsync(evidence with { Request = r }).AsTask();
        await f.Pump(ack);
        Assert.False(await ack);
        Assert.Same(before, f.Owner.Current);
    }

    [Fact]
    public async Task PhysicalFailureAtomicallyUnselectsAllAliasesAndPreservesOtherMembers()
    {
        await using var f = new ReviewFixture(aliases: true);
        await f.Open();
        await f.Eligible(1, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var before = f.Owner.Current;
        var ack = f.Owner.FailRevalidationAsync(f.Evidence(2).Request, ReviewUnavailableReason.Changed).AsTask();
        await f.Pump(ack);
        Assert.True(await ack);
        var group = Assert.Single(f.Owner.Current.Groups);
        Assert.All(group.Members.Take(2), m =>
        {
            Assert.Equal(ReviewEligibilityState.Unavailable, m.Eligibility);
            Assert.Equal(ReviewUnavailableReason.Changed, m.Reason);
            Assert.False(m.Selected);
        });
        Assert.Equal(ReviewEligibilityState.Eligible, group.Members[2].Eligibility);
        Assert.Empty(group.SelectedOccurrenceIds);
        Assert.Equal(before.SelectionRevision + 1, f.Owner.Current.SelectionRevision);
        Assert.Equal(2, f.Owner.Current.Unresolved.UnavailableReviewMembers);
        Assert.Equal(1, f.Owner.Current.Unresolved.UnselectedSurvivorMembers);
        Assert.Equal(2, f.Owner.Current.Unresolved.Total);
        var late = f.Owner.CommitRevalidationAsync(f.Evidence(2)).AsTask();
        await f.Pump(late);
        Assert.False(await late);
    }

    internal static ReviewOpenRequest MixedRequest(
        ReviewOpenRequest template,
        bool includeTerminalFailures)
    {
        var pin = template.Scan.Pin!;
        var context = new ScanRunContext(pin, template.Scope.Preflight!.Scope!);
        var analyzed = Enumerable.Range(1, 5)
            .Select(id => ReviewData.Request(id, context).Occurrence)
            .Select((member, index) => ReviewFixture.Copy(member,
                content: new(member.PhysicalFileKey!.Value, 1,
                    index < 2 ? ScanTestData.Digest : new string((char)('B' + index), 64))) with
            {
                VisualFeatures = ScanTestData.Success,
                NearDuplicate = ScanTestData.Success,
            })
            .ToImmutableArray();
        var exact = new CandidateGroup(Guid.NewGuid(), 1, pin, CandidateCategory.Exact,
            [analyzed[0], analyzed[1]],
            [new(analyzed[0].Token, analyzed[1].Token, analyzed[0].Content!, analyzed[1].Content!)],
            false, null, new(4096, RecoveryUnavailableReason.None));
        var near = new CandidateGroup(Guid.NewGuid(), 1, pin, CandidateCategory.NearDuplicate,
            [analyzed[0], analyzed[1], analyzed[2]], [], false, null,
            new(null, RecoveryUnavailableReason.InconsistentFacts))
        {
            Similarity =
            [
                new(analyzed[0].Token, analyzed[2].Token, 1, 2, NearRelationshipKind.Near),
                new(analyzed[1].Token, analyzed[2].Token, 1, 2, NearRelationshipKind.Near),
            ],
        };
        var uncertain = new CandidateGroup(Guid.NewGuid(), 1, pin, CandidateCategory.Uncertain,
            [analyzed[2], analyzed[3]], [], true, null,
            new(null, RecoveryUnavailableReason.InconsistentFacts))
        {
            Similarity =
            [
                new(analyzed[2].Token, analyzed[3].Token, 8, 20,
                    NearRelationshipKind.Uncertain),
            ],
        };
        var occurrences = analyzed;
        var counts = new ScanCounts(5, 5, 0, 0, 0, 0);
        if (includeTerminalFailures)
        {
            var unsupported = Enumerable.Range(6, 2).Select(id => UnsupportedRow(
                ReviewData.Request(id, context).Occurrence));
            var failed = new[]
            {
                FailedNearRow(ReviewData.Request(8, context).Occurrence, failVisualFeatures: true),
                FailedNearRow(ReviewData.Request(9, context).Occurrence, failVisualFeatures: false),
            };
            occurrences = [.. analyzed, .. unsupported, .. failed];
            counts = new(9, 5, 2, 2, 0, 0);
        }
        return template with
        {
            Scan = template.Scan with
            {
                Status = ScanStatus.Complete,
                Occurrences = occurrences,
                Groups = [exact, near, uncertain],
                Counts = counts,
            },
        };
    }

    private static ScanFileOccurrence UnsupportedRow(ScanFileOccurrence source) =>
        new(source.Token, source.DiscoveredPath, source.ResolvedPath, source.PhysicalFileKey,
            new(ScanStageState.Unsupported, ScanReason.UnsupportedExtension),
            new(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension), null,
            new(null, null, false))
        {
            VisualFeatures = new(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension),
            NearDuplicate = new(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension),
        };

    private static ScanFileOccurrence FailedNearRow(
        ScanFileOccurrence source,
        bool failVisualFeatures) => source with
        {
            VisualFeatures = failVisualFeatures
                ? new(ScanStageState.FailedRetryable, ScanReason.ReadFailure)
                : ScanTestData.Success,
            NearDuplicate = failVisualFeatures
                ? new(ScanStageState.NotApplicable, ScanReason.ReadFailure)
                : new(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
        };

    private static async Task<ApprovedActionRequest> Approve(ReviewFixture fixture)
    {
        var snapshot = fixture.Owner.Current;
        var approval = fixture.Owner.ApproveAsync(new(snapshot.Pin!, snapshot.SelectionRevision,
            Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await fixture.Pump(approval);
        var approved = await approval;
        Assert.Equal(ReviewCommandOutcome.Approved, approved.Outcome);
        return approved.ApprovedRequest!;
    }

    internal static ReviewOpenRequest Continuation(ReviewOpenRequest request, Guid continues,
        ImmutableArray<long> excluded = default, ImmutableArray<Guid> closed = default) => request with
        {
            ContinuesRequestId = continues,
            ExcludedOccurrenceIds = excluded.IsDefault ? ImmutableHashSet<long>.Empty : excluded.ToImmutableHashSet(),
            ClosedGroupIds = closed.IsDefault ? ImmutableHashSet<Guid>.Empty : closed.ToImmutableHashSet(),
        };

    private static ReviewMemberSnapshot Member(ReviewSnapshot snapshot, int groupIndex, long occurrenceId) =>
        snapshot.Groups[groupIndex].Members.Single(member => member.Occurrence.Token.OccurrenceId == occurrenceId);

    private static async Task CommitEvidence(
        ReviewFixture fixture,
        ReviewOpenRequest request,
        CandidateGroup group,
        ScanFileOccurrence member,
        AllocationEvidence? allocation = null)
    {
        var accepted = fixture.Owner.CommitRevalidationAsync(RevalidationEvidence(
            fixture.Owner.Current.Pin!, request, group, member, allocation)).AsTask();
        await fixture.Pump(accepted);
        Assert.True(await accepted);
    }

    private static ReviewRevalidationEvidence RevalidationEvidence(
        ReviewSessionPin session,
        ReviewOpenRequest request,
        CandidateGroup group,
        ScanFileOccurrence member,
        AllocationEvidence? allocation = null)
    {
        var revalidation = new ReviewRevalidationRequest(session,
            new(request.Scan.Pin!, request.Scope.Preflight!.Scope!), group.GroupId,
            group.GroupRevision, member, 1);
        var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value,
            member.Content!.ByteLength, member.ResolvedPath, null,
            allocation ?? member.Allocation);
        return new(revalidation, facts, facts,
            new(member.Content.ByteLength, member.Content.Sha256));
    }

    private static async Task<ReviewCommandResult> Change(
        ReviewFixture fixture,
        int groupIndex,
        SelectionCommandKind kind,
        params long[] occurrenceIds)
    {
        var snapshot = fixture.Owner.Current;
        var group = snapshot.Groups[groupIndex].Candidate;
        var changed = fixture.Owner.ChangeSelectionAsync(new(new(snapshot.Pin!, group.GroupId,
            group.GroupRevision, snapshot.SelectionRevision), kind, [.. occurrenceIds],
            Guid.NewGuid()), TestContext.Current.CancellationToken);
        await fixture.Pump(changed);
        return await changed;
    }
}

internal sealed class ReviewFixture : IAsyncDisposable
{
    internal ManualScanScheduler Scheduler { get; } = new();
    internal ReviewExecutor Executor { get; } = new();
    internal ReviewCoordinator Owner { get; }
    internal ReviewOpenRequest Request { get; }
    internal ReviewFixture(bool aliases = false)
    {
        var context = ScanTestData.Context();
        var members = Enumerable.Range(1, 3).Select(id => ReviewData.Request(id, context).Occurrence).ToImmutableArray();
        if (aliases)
        {
            members = members.SetItem(0, Copy(members[0], allocation: new(4096, 2, true)));
            members = members.SetItem(1, Copy(members[1], key: members[0].PhysicalFileKey,
                content: members[0].Content, allocation: members[0].Allocation));
        }
        var group = new CandidateGroup(Guid.NewGuid(), 1, context.Pin, CandidateCategory.Exact,
            members, ExactEvidence(members), false, null,
            new(99999, RecoveryUnavailableReason.None));
        var evidence = new ScopePreflightEvidence(context.Scope, ScopeVolumeKind.Fixed, ScopeCloudState.Local,
            ScopeTraversalState.Complete, new(1, 0, 0, 0), new(0, 0));
        var scope = new ShellSnapshot([], "Ready", ScopeSessionState.Ready, context.Scope.Selection,
            ScopePreflightPolicy.Evaluate(evidence), Revision: 1);
        Request = new(scope, new(1, 1), new(context.Pin, ScanStatus.Complete, ScanTestData.Success, members, [group], new(3, 3, 0, 0, 0, 0)));
        Owner = new(Scheduler, Executor, new RejectEvidenceStore(), resultCapacity: 1);
    }
    internal Task Pump(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));
    internal static ScanFileOccurrence Copy(ScanFileOccurrence member, OccurrenceToken? token = null,
        PhysicalFileKey? key = null, ContentRevision? content = null, AllocationEvidence? allocation = null) =>
        new(token ?? member.Token, member.DiscoveredPath, member.ResolvedPath, key ?? member.PhysicalFileKey,
            member.Enumerate, member.Exact, content ?? member.Content, allocation ?? member.Allocation);
    internal static ImmutableArray<EqualityEvidence> ExactEvidence(
        ImmutableArray<ScanFileOccurrence> members)
    {
        var representatives = members.GroupBy(member => member.PhysicalFileKey!.Value)
            .Select(group => group.First()).ToArray();
        return representatives.Skip(1).Select(member => new EqualityEvidence(
            representatives[0].Token, member.Token,
            representatives[0].Content!, member.Content!)).ToImmutableArray();
    }
    internal async Task<ReviewCommandResult> Open(ReviewOpenRequest? request = null)
    {
        var task = Owner.OpenAsync(request ?? Request, TestContext.Current.CancellationToken);
        await Pump(task);
        return await task;
    }
    internal async Task<ReviewTestRun> Run()
    {
        Assert.NotNull(Owner.Current.Pin);
        await Pump(Executor.Arrived.Task);
        return await Executor.Arrived.Task;
    }
    internal SelectionCommand Command(SelectionCommandKind kind, params long[] ids)
    {
        var snapshot = Owner.Current;
        var group = snapshot.Groups[0].Candidate;
        return new(new(snapshot.Pin!, group.GroupId, group.GroupRevision, snapshot.SelectionRevision), kind, [.. ids], Guid.NewGuid());
    }
    internal ReviewRevalidationEvidence Evidence(long id)
    {
        var group = Request.Scan.Groups[0];
        var member = group.Members.Single(m => m.Token.OccurrenceId == id);
        var request = new ReviewRevalidationRequest(Owner.Current.Pin!, new(Request.Scan.Pin!, Request.Scope.Preflight!.Scope!), group.GroupId, group.GroupRevision, member, 1);
        var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value, 1, member.ResolvedPath, null, member.Allocation);
        return new(request, facts, facts, new(1, ScanTestData.Digest));
    }
    internal async Task Eligible(params long[] ids)
    {
        await Run();
        foreach (var id in ids)
        {
            var task = Owner.CommitRevalidationAsync(Evidence(id)).AsTask();
            await Pump(task);
            Assert.True(await task);
        }
    }
    internal async Task Eligible(ReviewOpenRequest request)
    {
        await Run();
        foreach (var group in request.Scan.Groups)
        foreach (var member in group.Members)
        {
            var revalidation = new ReviewRevalidationRequest(Owner.Current.Pin!,
                new(request.Scan.Pin!, request.Scope.Preflight!.Scope!), group.GroupId,
                group.GroupRevision, member, 1);
            var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value,
                member.Content!.ByteLength, member.ResolvedPath, null, member.Allocation);
            var task = Owner.CommitRevalidationAsync(new(revalidation, facts, facts,
                new(member.Content.ByteLength, member.Content.Sha256))).AsTask();
            await Pump(task);
            Assert.True(await task);
        }
    }
    internal async Task<ReviewCommandResult> Change(SelectionCommand command)
    {
        var task = Owner.ChangeSelectionAsync(command, TestContext.Current.CancellationToken);
        await Pump(task);
        return await task;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var run in Executor.Runs) run.Done.TrySetResult();
        await Pump(Owner.DisposeAsync().AsTask());
    }
    private sealed class RejectEvidenceStore : IOverrideEvidenceStore
    {
        public ValueTask<OverridePersistResult> PersistAsync(DeliberateOverrideEvidence evidence, CancellationToken cancellationToken) => throw new InvalidOperationException("Task 4 must not persist");
        public ValueTask<OverrideReadResult> ReadAsync(OverrideEvidenceId evidenceId, CancellationToken cancellationToken) => throw new InvalidOperationException("Task 4 must not read override authority");
    }
}

internal sealed class ReviewExecutor : IReviewRevalidationExecutor
{
    internal ConcurrentQueue<ReviewTestRun> Runs { get; } = new();
    internal TaskCompletionSource<ReviewTestRun> Arrived { get; set; } = ScanTestData.Signal<ReviewTestRun>();
    public IReviewRun Start(ReviewSessionPin session, ScanRunContext context, ImmutableArray<CandidateGroup> groups, IReviewResultSink sink)
    {
        var run = new ReviewTestRun(session, sink) { Groups = groups };
        Runs.Enqueue(run);
        Arrived.TrySetResult(run);
        return run;
    }
}

internal sealed class ReviewTestRun(ReviewSessionPin pin, IReviewResultSink sink) : IReviewRun
{
    internal ReviewSessionPin Pin { get; } = pin;
    internal IReviewResultSink Sink { get; } = sink;
    internal ImmutableArray<CandidateGroup> Groups { get; init; } = [];
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<Task>? OnCancel { get; set; }
    internal Exception? DisposalFailure { get; set; }
    public Task Completion => Done.Task;
    public Task CancelAsync() { Done.TrySetResult(); return OnCancel?.Invoke() ?? Task.CompletedTask; }
    public ValueTask DisposeAsync()
    {
        Disposed.TrySetResult();
        return DisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalFailure);
    }
}
