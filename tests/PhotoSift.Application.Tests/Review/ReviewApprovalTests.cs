using System.Collections.Concurrent;
using System.Collections.Immutable;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ReviewApprovalTests
{
    [Fact]
    public async Task ExplicitApprovalSealsOnceAndReturnsTheSameRequestForTheIdenticalActivation()
    {
        var evidenceStore = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(evidenceStore);
        await fixture.OpenSelectedAsync();
        var before = fixture.Owner.Current;
        var command = fixture.ApprovalCommand();

        var first = await fixture.ApproveAsync(command);
        var repeated = await fixture.ApproveAsync(command);

        Assert.Equal(ReviewCommandOutcome.Approved, first.Outcome);
        Assert.Equal(before.SelectionRevision + 1, first.Snapshot.SelectionRevision);
        Assert.True(first.Snapshot.Sealed);
        Assert.All(first.Snapshot.Groups, group => Assert.True(group.Sealed));
        Assert.Same(first.ApprovedRequest, first.Snapshot.ApprovedRequest);
        Assert.Equal(ReviewCommandOutcome.Approved, repeated.Outcome);
        Assert.Same(first.ApprovedRequest, repeated.ApprovedRequest);
        Assert.Same(first.Snapshot, repeated.Snapshot);
        Assert.Empty(evidenceStore.ReadEvidenceIds);
    }

    [Fact]
    public async Task ASealedSelectionRejectsMutationOverrideAndAnotherApprovalKey()
    {
        var evidenceStore = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(evidenceStore);
        await fixture.OpenSelectedAsync();
        var acceptedCommand = fixture.ApprovalCommand();
        var approved = await fixture.ApproveAsync(acceptedCommand);
        var sealedRevision = approved.Snapshot.SelectionRevision;
        var mutation = fixture.SelectionCommand(0, SelectionCommandKind.Toggle, 3);

        var changed = await fixture.ChangeAsync(mutation);
        var affirmation = await fixture.AffirmAsync(new(
            fixture.ProposalForCurrentSelection(), new(Guid.NewGuid()), DateTimeOffset.UtcNow));
        var anotherApproval = await fixture.ApproveAsync(
            acceptedCommand with { IdempotencyKey = Guid.NewGuid() });

        Assert.Equal(ReviewCommandOutcome.Sealed, changed.Outcome);
        Assert.Equal(ReviewCommandOutcome.Sealed, affirmation.Outcome);
        Assert.Equal(ReviewCommandOutcome.Sealed, anotherApproval.Outcome);
        Assert.Equal(sealedRevision, fixture.Owner.Current.SelectionRevision);
        Assert.Same(approved.ApprovedRequest, fixture.Owner.Current.ApprovedRequest);
        Assert.Empty(evidenceStore.PersistedEvidence);
    }

    [Fact]
    public async Task ReusingTheAcceptedKeyWithDifferentActivationDataIsNotIdempotent()
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        await fixture.OpenSelectedAsync();
        var command = fixture.ApprovalCommand();
        var approved = await fixture.ApproveAsync(command);

        var changedActivation = await fixture.ApproveAsync(
            command with { ApprovedUtc = command.ApprovedUtc.AddTicks(1) });

        Assert.Equal(ReviewCommandOutcome.Sealed, changedActivation.Outcome);
        Assert.Same(approved.ApprovedRequest, changedActivation.Snapshot.ApprovedRequest);
        Assert.Equal(approved.Snapshot.SelectionRevision,
            changedActivation.Snapshot.SelectionRevision);
    }

    [Fact]
    public async Task ApprovalReadsOverrideEvidenceOutsideTheOwnerTurnAndSealsOnlyAfterTheBarrierReleases()
    {
        var evidenceStore = new DelayedApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(evidenceStore);
        await fixture.OpenOverrideSelectedAsync();
        var command = fixture.ApprovalCommand();

        var pending = fixture.Owner.ApproveAsync(command, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(evidenceStore.ReadEntered.Task);
        var readId = await evidenceStore.ReadEntered.Task;

        Assert.Equal(fixture.OverrideEvidence!.EvidenceId, readId);
        Assert.False(pending.IsCompleted);
        Assert.False(fixture.Owner.Current.Sealed);
        evidenceStore.Release();
        await fixture.PumpAsync(pending);
        var result = await pending;

        Assert.Equal(ReviewCommandOutcome.Approved, result.Outcome);
        Assert.True(result.Snapshot.Sealed);
        Assert.Same(result.ApprovedRequest, result.Snapshot.ApprovedRequest);
    }

    [Fact]
    public async Task SelectionChangeDuringEvidenceReadMakesTheCapturedApprovalStale()
    {
        var evidenceStore = new DelayedApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(evidenceStore);
        await fixture.OpenOverrideSelectedAsync();
        var command = fixture.ApprovalCommand();
        var pending = fixture.Owner.ApproveAsync(command, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(evidenceStore.ReadEntered.Task);

        var changed = fixture.Owner.ChangeSelectionAsync(
            fixture.SelectionCommand(0, SelectionCommandKind.Toggle, 1),
            TestContext.Current.CancellationToken);
        await fixture.PumpAsync(changed);
        Assert.Equal(ReviewCommandOutcome.Committed, (await changed).Outcome);
        evidenceStore.Release();
        await fixture.PumpAsync(pending);
        var result = await pending;

        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.False(result.Snapshot.Sealed);
        Assert.Null(result.Snapshot.ApprovedRequest);
    }
}

internal sealed class ApprovalFixture : IAsyncDisposable
{
    private static readonly DateTimeOffset ApprovedUtc =
        new(2026, 9, 9, 20, 15, 30, TimeSpan.FromHours(3));
    private readonly ApprovalEvidenceStore evidenceStore;

    internal ApprovalFixture(ApprovalEvidenceStore evidenceStore, ScanStatus coverage = ScanStatus.Complete,
        ProtectedActionSafetyValidator? actionSafety = null)
    {
        this.evidenceStore = evidenceStore;
        var context = ScanTestData.Context();
        var first = Enumerable.Range(1, 3)
            .Select(id => ReviewData.Request(id, context).Occurrence)
            .ToImmutableArray();
        first = first.SetItem(0, ReviewFixture.Copy(first[0], allocation: new(4096, 2, true)));
        first = first.SetItem(1, ReviewFixture.Copy(first[1], key: first[0].PhysicalFileKey,
            content: first[0].Content, allocation: first[0].Allocation));
        var second = Enumerable.Range(4, 2)
            .Select(id => ReviewData.Request(id, context).Occurrence)
            .ToImmutableArray();
        var firstGroup = new CandidateGroup(
            Guid.Parse("75ad325f-78ed-4c94-aa49-7e30b70c36c8"), 3, context.Pin,
            CandidateCategory.Exact, first, ReviewFixture.ExactEvidence(first), false, null,
            new(99999, RecoveryUnavailableReason.None));
        var secondGroup = new CandidateGroup(
            Guid.Parse("ef4ce190-1cbd-407e-8f06-a6e913d647f1"), 7, context.Pin,
            CandidateCategory.Exact, second, ReviewFixture.ExactEvidence(second), false, null,
            new(99999, RecoveryUnavailableReason.None));
        var scopeEvidence = new ScopePreflightEvidence(context.Scope, ScopeVolumeKind.Fixed,
            ScopeCloudState.Local, ScopeTraversalState.Complete, new(1, 0, 0, 0), new(0, 0));
        var scope = new ShellSnapshot([], "Ready", ScopeSessionState.Ready, context.Scope.Selection,
            ScopePreflightPolicy.Evaluate(scopeEvidence), Revision: 1);
        var enumeration = coverage == ScanStatus.Incomplete
            ? new StageOutcome(ScanStageState.FailedTerminal, ScanReason.IncompleteEnumeration)
            : ScanTestData.Success;
        var counts = coverage == ScanStatus.Incomplete
            ? new ScanCounts(5, 5, 0, 2, 3, 4)
            : new ScanCounts(5, 5, 0, 0, 0, 0);
        Request = new(scope, new(1, 1), new(context.Pin, coverage, enumeration,
            [.. first, .. second], [firstGroup, secondGroup], counts));
        Owner = new(Scheduler, Executor, evidenceStore, resultCapacity: 1, actionSafety: actionSafety);
    }

    internal ManualScanScheduler Scheduler { get; } = new();
    internal ReviewExecutor Executor { get; } = new();
    internal ReviewCoordinator Owner { get; }
    internal ReviewOpenRequest Request { get; private set; }
    internal DeliberateOverrideEvidence? OverrideEvidence { get; private set; }

    internal Task PumpAsync(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));

    internal async Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest? request = null)
    {
        var opened = Owner.OpenAsync(request ?? Request, TestContext.Current.CancellationToken);
        await PumpAsync(opened);
        return await opened;
    }

    internal async Task OpenSelectedAsync(bool leaveSecondGroupUnresolved = false)
    {
        Assert.Equal(ReviewCommandOutcome.Committed, (await OpenAsync()).Outcome);
        await PumpAsync(Executor.Arrived.Task);
        await RevalidateAsync(1, 2, 3);
        if (leaveSecondGroupUnresolved)
        {
            await FailAsync(5, ReviewUnavailableReason.Missing);
        }
        else
        {
            await RevalidateAsync(4, 5);
        }

        var selected = await ChangeAsync(SelectionCommand(0,
            SelectionCommandKind.ReplaceFocusedGroup, 2, 1));
        Assert.Equal(ReviewCommandOutcome.Committed, selected.Outcome);
    }

    internal async Task OpenOverrideSelectedAsync()
    {
        Assert.Equal(ReviewCommandOutcome.Committed, (await OpenAsync()).Outcome);
        await PumpAsync(Executor.Arrived.Task);
        await RevalidateAsync(1, 2, 3);
        var proposalResult = await ChangeAsync(SelectionCommand(0,
            SelectionCommandKind.ReplaceFocusedGroup, 3, 1, 2));
        Assert.Equal(ReviewCommandOutcome.OverrideRequired, proposalResult.Outcome);
        var evidenceId = new OverrideEvidenceId(Guid.Parse("0a70e6aa-0186-4654-9ed7-d3ae574f1552"));
        var affirmed = await AffirmAsync(new(proposalResult.OverrideProposal!, evidenceId,
            new DateTimeOffset(2026, 9, 9, 17, 30, 0, TimeSpan.Zero)));
        Assert.Equal(ReviewCommandOutcome.Committed, affirmed.Outcome);
        OverrideEvidence = Assert.Single(evidenceStore.PersistedEvidence);
    }

    internal async Task RevalidateAsync(params long[] occurrenceIds)
    {
        foreach (var occurrenceId in occurrenceIds)
        {
            var accepted = Owner.CommitRevalidationAsync(Evidence(occurrenceId)).AsTask();
            await PumpAsync(accepted);
            Assert.True(await accepted);
        }
    }

    internal async Task FailAsync(long occurrenceId, ReviewUnavailableReason reason)
    {
        var failed = Owner.FailRevalidationAsync(Evidence(occurrenceId).Request, reason).AsTask();
        await PumpAsync(failed);
        Assert.True(await failed);
    }

    internal SelectionCommand SelectionCommand(
        int groupIndex,
        SelectionCommandKind kind,
        params long[] occurrenceIds)
    {
        var snapshot = Owner.Current;
        var group = snapshot.Groups[groupIndex].Candidate;
        return new(new(snapshot.Pin!, group.GroupId, group.GroupRevision, snapshot.SelectionRevision),
            kind, [.. occurrenceIds], Guid.NewGuid());
    }

    internal ApproveSelectionCommand ApprovalCommand(
        Guid? idempotencyKey = null,
        DateTimeOffset? approvedUtc = null)
    {
        var snapshot = Owner.Current;
        return new(snapshot.Pin!, snapshot.SelectionRevision,
            idempotencyKey ?? Guid.Parse("46928bbc-bfaa-46c0-a8fb-b962a8da3ea7"),
            approvedUtc ?? ApprovedUtc);
    }

    internal OverrideProposalToken ProposalForCurrentSelection()
    {
        var snapshot = Owner.Current;
        var group = snapshot.Groups[0];
        return new(Guid.NewGuid(), new(snapshot.Pin!, group.Candidate.GroupId,
                group.Candidate.GroupRevision, snapshot.SelectionRevision),
            group.SelectedOccurrenceIds.Order().ToImmutableArray(),
            SurvivorPolicy.Version, "survivor-consequence-v1");
    }

    internal async Task<ReviewCommandResult> ChangeAsync(SelectionCommand command)
    {
        var changed = Owner.ChangeSelectionAsync(command, TestContext.Current.CancellationToken);
        await PumpAsync(changed);
        return await changed;
    }

    internal async Task<ReviewCommandResult> AffirmAsync(OverrideAffirmationCommand command)
    {
        var affirmed = Owner.AffirmOverrideAsync(command, TestContext.Current.CancellationToken);
        await PumpAsync(affirmed);
        return await affirmed;
    }

    internal async Task<ReviewCommandResult> ApproveAsync(ApproveSelectionCommand command)
    {
        var approved = Owner.ApproveAsync(command, TestContext.Current.CancellationToken);
        await PumpAsync(approved);
        return await approved;
    }

    internal ReviewRevalidationEvidence Evidence(long occurrenceId)
    {
        var group = Request.Scan.Groups.Single(value =>
            value.Members.Any(member => member.Token.OccurrenceId == occurrenceId));
        var member = group.Members.Single(value => value.Token.OccurrenceId == occurrenceId);
        var request = new ReviewRevalidationRequest(Owner.Current.Pin!,
            new(Request.Scan.Pin!, Request.Scope.Preflight!.Scope!), group.GroupId,
            group.GroupRevision, member, 1);
        var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value,
            member.Content!.ByteLength, member.ResolvedPath, null, member.Allocation);
        return new(request, facts, facts,
            new(member.Content.ByteLength, member.Content.Sha256));
    }

    internal void ReplaceRequest(ReviewOpenRequest request) => Request = request;

    public async ValueTask DisposeAsync()
    {
        foreach (var run in Executor.Runs)
        {
            run.Done.TrySetResult();
        }

        await PumpAsync(Owner.DisposeAsync().AsTask());
    }
}

internal class ApprovalEvidenceStore : IOverrideEvidenceStore
{
    internal ConcurrentQueue<DeliberateOverrideEvidence> PersistedEvidence { get; } = new();
    internal ConcurrentQueue<OverrideEvidenceId> ReadEvidenceIds { get; } = new();
    internal Func<OverrideEvidenceId, CancellationToken, ValueTask<OverrideReadResult>>? Read { get; set; }

    public ValueTask<OverridePersistResult> PersistAsync(
        DeliberateOverrideEvidence evidence,
        CancellationToken cancellationToken)
    {
        PersistedEvidence.Enqueue(evidence);
        return ValueTask.FromResult(new OverridePersistResult(OverridePersistOutcome.Persisted));
    }

    public virtual ValueTask<OverrideReadResult> ReadAsync(
        OverrideEvidenceId evidenceId,
        CancellationToken cancellationToken)
    {
        ReadEvidenceIds.Enqueue(evidenceId);
        if (Read is not null)
        {
            return Read(evidenceId, cancellationToken);
        }

        var evidence = PersistedEvidence.SingleOrDefault(value => value.EvidenceId == evidenceId);
        return ValueTask.FromResult(evidence is null
            ? new OverrideReadResult(OverrideReadOutcome.Missing, null)
            : new OverrideReadResult(OverrideReadOutcome.Found, evidence));
    }
}

internal sealed class DelayedApprovalEvidenceStore : ApprovalEvidenceStore
{
    private readonly TaskCompletionSource<OverrideReadResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<OverrideEvidenceId> ReadEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DelayedApprovalEvidenceStore()
    {
        Read = DelayAsync;
    }

    internal void Release()
    {
        var evidence = Assert.Single(PersistedEvidence);
        completion.TrySetResult(new(OverrideReadOutcome.Found, evidence));
    }

    private ValueTask<OverrideReadResult> DelayAsync(
        OverrideEvidenceId evidenceId,
        CancellationToken cancellationToken)
    {
        ReadEntered.TrySetResult(evidenceId);
        return new(completion.Task);
    }
}
