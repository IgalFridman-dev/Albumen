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

public sealed class ReviewOverrideTests
{
    [Theory]
    [InlineData(OverridePersistOutcome.Persisted)]
    [InlineData(OverridePersistOutcome.ExactRetry)]
    public async Task DurableCurrentReceiptCommitsTheExactFinalSelectionOnce(OverridePersistOutcome outcome)
    {
        var evidenceStore = new RecordingOverrideEvidenceStore { Outcome = outcome };
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        var before = fixture.Owner.Current;
        var evidenceId = new OverrideEvidenceId(Guid.Parse("094055d6-c9c7-4e97-9794-2bdd4f6d9915"));
        var affirmedUtc = new DateTimeOffset(2026, 9, 9, 17, 23, 45, TimeSpan.Zero);

        var result = await fixture.AffirmAsync(new(proposal, evidenceId, affirmedUtc));

        Assert.Equal(ReviewCommandOutcome.Committed, result.Outcome);
        Assert.Equal(before.SelectionRevision + 1, result.Snapshot.SelectionRevision);
        var group = Assert.Single(result.Snapshot.Groups);
        Assert.Equal([1L, 2L, 3L], group.SelectedOccurrenceIds);
        Assert.All(group.Members, member => Assert.True(member.Selected));
        Assert.Equal(evidenceId, group.OverrideEvidenceId);
        Assert.False(result.Snapshot.Sealed);
        var persisted = Assert.Single(evidenceStore.Evidence);
        Assert.Equal(evidenceId, persisted.EvidenceId);
        Assert.Equal(affirmedUtc, persisted.AffirmedUtc);
        Assert.Equal(proposal.Pin, persisted.Pin);
        Assert.Equal([1L, 2L, 3L], persisted.SelectedOccurrenceIds);
        Assert.Equal(SurvivorPolicy.Version, persisted.SurvivorPolicyVersion);
        Assert.Equal("survivor-consequence-v1", persisted.ConsequenceTextVersion);
        Assert.True(persisted.DeliberatelyAffirmed);
        Assert.Equal(
            fixture.Request.Scan.Groups[0].Members.Select(member => new AffectedPhysicalRevision(
                member.PhysicalFileKey!.Value, member.Content!, member.Allocation)).OrderBy(AffectedKey),
            persisted.AffectedPhysicalRevisions);
    }

    [Fact]
    public async Task EquivalentProposalWithRebuiltImmutableArrayRemainsTheExactIssuedValue()
    {
        var evidenceStore = new RecordingOverrideEvidenceStore();
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        var rebuilt = proposal with
        {
            ProposedSelectedOccurrenceIds = [.. proposal.ProposedSelectedOccurrenceIds],
        };

        var result = await fixture.AffirmAsync(new(rebuilt, new(Guid.NewGuid()), DateTimeOffset.UtcNow));

        Assert.Equal(ReviewCommandOutcome.Committed, result.Outcome);
        Assert.Single(evidenceStore.Evidence);
    }

    [Fact]
    public async Task UnchangedRevalidationCompletionDoesNotInvalidateAnIssuedProposal()
    {
        var evidenceStore = new RecordingOverrideEvidenceStore();
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        await fixture.CompleteRevalidationRunAsync();

        var result = await fixture.AffirmAsync(
            new(proposal, new(Guid.NewGuid()), DateTimeOffset.UtcNow));

        Assert.Equal(ReviewCommandOutcome.Committed, result.Outcome);
        Assert.Single(evidenceStore.Evidence);
    }

    [Theory]
    [InlineData(OverridePersistOutcome.Conflict)]
    [InlineData(OverridePersistOutcome.Unavailable)]
    public async Task PersistenceFailureLeavesPriorSelectionAndRevisionUntouched(OverridePersistOutcome outcome)
    {
        var evidenceStore = new RecordingOverrideEvidenceStore { Outcome = outcome };
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        var before = fixture.Owner.Current;

        var result = await fixture.AffirmAsync(new(proposal, new(Guid.NewGuid()), DateTimeOffset.UtcNow));

        Assert.Equal(ReviewCommandOutcome.PersistenceFailed, result.Outcome);
        Assert.Same(before, result.Snapshot);
        Assert.Empty(Assert.Single(result.Snapshot.Groups).SelectedOccurrenceIds);
        Assert.Null(Assert.Single(result.Snapshot.Groups).OverrideEvidenceId);
    }

    [Fact]
    public async Task PersistedReceiptAfterReviewReplacementNeverCommitsSelection()
    {
        var evidenceStore = new DelayedOverrideEvidenceStore();
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        var pending = fixture.Owner.AffirmOverrideAsync(
            new(proposal, new(Guid.NewGuid()), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await fixture.PumpAsync(evidenceStore.PersistEntered.Task);
        var stored = await evidenceStore.PersistEntered.Task;

        await fixture.OpenReplacementAsync(groupRevision: 2);
        evidenceStore.Complete(OverridePersistOutcome.Persisted);
        await fixture.PumpAsync(pending);
        var result = await pending;

        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Equal(fixture.Owner.Current.Pin, result.Snapshot.Pin);
        Assert.NotEqual(proposal.Pin.Session, result.Snapshot.Pin);
        Assert.All(result.Snapshot.Groups, group =>
        {
            Assert.Empty(group.SelectedOccurrenceIds);
            Assert.Null(group.OverrideEvidenceId);
        });
        Assert.Equal(proposal.Pin, stored.Pin);
    }

    [Fact]
    public async Task SelectionCanAdvanceWhilePersistenceWaitsAndMakesTheReceiptStale()
    {
        var evidenceStore = new DelayedOverrideEvidenceStore();
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        var pending = fixture.Owner.AffirmOverrideAsync(
            new(proposal, new(Guid.NewGuid()), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await fixture.PumpAsync(evidenceStore.PersistEntered.Task);

        var currentCommand = fixture.Command(SelectionCommandKind.Toggle, 1);
        var safe = fixture.Owner.ChangeSelectionAsync(currentCommand, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(safe);
        Assert.Equal(ReviewCommandOutcome.Committed, (await safe).Outcome);
        evidenceStore.Complete(OverridePersistOutcome.Persisted);
        await fixture.PumpAsync(pending);
        var stale = await pending;

        Assert.Equal(ReviewCommandOutcome.Stale, stale.Outcome);
        Assert.Equal([1L], Assert.Single(stale.Snapshot.Groups).SelectedOccurrenceIds);
        Assert.Null(Assert.Single(stale.Snapshot.Groups).OverrideEvidenceId);
    }

    [Theory]
    [InlineData("proposal")]
    [InlineData("review")]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("group")]
    [InlineData("group-revision")]
    [InlineData("selection")]
    [InlineData("occurrences")]
    [InlineData("policy")]
    [InlineData("consequence")]
    public async Task ForgedOrStaleProposalNeverReachesPersistence(string mismatch)
    {
        var evidenceStore = new RecordingOverrideEvidenceStore();
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();
        var pin = proposal.Pin;
        proposal = mismatch switch
        {
            "proposal" => proposal with { ProposalId = Guid.NewGuid() },
            "review" => proposal with { Pin = pin with { Session = pin.Session with { ReviewSessionId = Guid.NewGuid() } } },
            "scan" => proposal with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { ScanId = Guid.NewGuid() } } } },
            "scope" => proposal with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { ScopeRevision = 99 } } } },
            "rules" => proposal with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { RuleSetRevision = 99 } } } },
            "analysis" => proposal with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { AnalysisRevision = 99 } } } },
            "group" => proposal with { Pin = pin with { GroupId = Guid.NewGuid() } },
            "group-revision" => proposal with { Pin = pin with { GroupRevision = 99 } },
            "selection" => proposal with { Pin = pin with { SelectionRevision = 99 } },
            "occurrences" => proposal with { ProposedSelectedOccurrenceIds = [1, 2] },
            "policy" => proposal with { SurvivorPolicyVersion = "survivor-v0" },
            _ => proposal with { ConsequenceTextVersion = "unversioned" },
        };

        var result = await fixture.AffirmAsync(new(proposal, new(Guid.NewGuid()), DateTimeOffset.UtcNow));

        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Empty(evidenceStore.Evidence);
        Assert.Empty(Assert.Single(result.Snapshot.Groups).SelectedOccurrenceIds);
    }

    [Fact]
    public async Task InvalidAffirmationIdentityFailsBeforePersistence()
    {
        var evidenceStore = new RecordingOverrideEvidenceStore();
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();

        var result = await fixture.AffirmAsync(new(proposal, new(Guid.Empty), DateTimeOffset.UtcNow));

        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Empty(evidenceStore.Evidence);
    }

    [Fact]
    public async Task ThrowingPersistencePortFaultsTheCurrentReviewWithoutAttachingEvidence()
    {
        var evidenceStore = new RecordingOverrideEvidenceStore
        {
            Persist = (_, _) => ValueTask.FromException<OverridePersistResult>(
                new InvalidOperationException("injected programming fault")),
        };
        await using var fixture = new ReviewOverrideFixture(evidenceStore);
        var proposal = await fixture.OpenEligibleOverrideProposalAsync();

        var result = await fixture.AffirmAsync(new(proposal, new(Guid.NewGuid()), DateTimeOffset.UtcNow));

        Assert.Equal(ReviewCommandOutcome.Faulted, result.Outcome);
        Assert.Equal(ReviewStatus.Faulted, result.Snapshot.Status);
        Assert.Equal(ReviewUnavailableReason.ProgrammingFault, result.Snapshot.FaultReason);
        Assert.Empty(Assert.Single(result.Snapshot.Groups).SelectedOccurrenceIds);
        Assert.Null(Assert.Single(result.Snapshot.Groups).OverrideEvidenceId);
    }

    private static string AffectedKey(AffectedPhysicalRevision value) =>
        $"{value.PhysicalFileKey.VolumeSerial:X16}:{value.PhysicalFileKey.FileId.High:X16}:{value.PhysicalFileKey.FileId.Low:X16}";
}

internal sealed class ReviewOverrideFixture : IAsyncDisposable
{
    internal ReviewOverrideFixture(IOverrideEvidenceStore evidenceStore)
    {
        var context = ScanTestData.Context();
        var members = Enumerable.Range(1, 3)
            .Select(id => ReviewData.Request(id, context).Occurrence)
            .ToImmutableArray();
        var group = new CandidateGroup(Guid.Parse("540482d2-25bb-4e9e-ad4f-aed85c6fe354"), 1,
            context.Pin, CandidateCategory.Exact, members, ReviewFixture.ExactEvidence(members), false, null,
            new(99999, RecoveryUnavailableReason.None));
        var scopeEvidence = new ScopePreflightEvidence(context.Scope, ScopeVolumeKind.Fixed,
            ScopeCloudState.Local, ScopeTraversalState.Complete, new(1, 0, 0, 0), new(0, 0));
        var scope = new ShellSnapshot([], "Ready", ScopeSessionState.Ready, context.Scope.Selection,
            ScopePreflightPolicy.Evaluate(scopeEvidence), Revision: 1);
        Request = new(scope, new(1, 1), new(context.Pin, ScanStatus.Complete,
            ScanTestData.Success, members, [group], new(3, 3, 0, 0, 0, 0)));
        Owner = new(Scheduler, Executor, evidenceStore, resultCapacity: 1);
    }

    internal ManualScanScheduler Scheduler { get; } = new();
    internal ReviewExecutor Executor { get; } = new();
    internal ReviewCoordinator Owner { get; }
    internal ReviewOpenRequest Request { get; private set; }

    internal Task PumpAsync(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));

    internal async Task<OverrideProposalToken> OpenEligibleOverrideProposalAsync()
    {
        var opened = Owner.OpenAsync(Request, TestContext.Current.CancellationToken);
        await PumpAsync(opened);
        Assert.Equal(ReviewCommandOutcome.Committed, (await opened).Outcome);
        await PumpAsync(Executor.Arrived.Task);
        foreach (var id in new long[] { 1, 2, 3 })
        {
            var accepted = Owner.CommitRevalidationAsync(Evidence(id)).AsTask();
            await PumpAsync(accepted);
            Assert.True(await accepted);
        }
        var proposed = Owner.ChangeSelectionAsync(
            Command(SelectionCommandKind.ReplaceFocusedGroup, 3, 1, 2),
            TestContext.Current.CancellationToken);
        await PumpAsync(proposed);
        var result = await proposed;
        Assert.Equal(ReviewCommandOutcome.OverrideRequired, result.Outcome);
        Assert.Empty(Assert.Single(result.Snapshot.Groups).SelectedOccurrenceIds);
        return Assert.IsType<OverrideProposalToken>(result.OverrideProposal);
    }

    internal async Task<ReviewCommandResult> AffirmAsync(OverrideAffirmationCommand command)
    {
        var pending = Owner.AffirmOverrideAsync(command, TestContext.Current.CancellationToken);
        await PumpAsync(pending);
        return await pending;
    }

    internal SelectionCommand Command(SelectionCommandKind kind, params long[] occurrenceIds)
    {
        var snapshot = Owner.Current;
        var group = Assert.Single(snapshot.Groups).Candidate;
        return new(new(snapshot.Pin!, group.GroupId, group.GroupRevision, snapshot.SelectionRevision),
            kind, [.. occurrenceIds], Guid.NewGuid());
    }

    internal async Task OpenReplacementAsync(long groupRevision)
    {
        Executor.Arrived = ScanTestData.Signal<ReviewTestRun>();
        var group = Assert.Single(Request.Scan.Groups) with { GroupRevision = groupRevision };
        Request = Request with { Scan = Request.Scan with { Groups = [group] } };
        var opened = Owner.OpenAsync(Request, TestContext.Current.CancellationToken);
        await PumpAsync(opened);
        Assert.Equal(ReviewCommandOutcome.Committed, (await opened).Outcome);
    }

    internal async Task CompleteRevalidationRunAsync()
    {
        var published = ScanTestData.Signal<ReviewSnapshot>();
        void Capture(object? _, ReviewSnapshot snapshot) => published.TrySetResult(snapshot);
        Owner.SnapshotChanged += Capture;
        try
        {
            Assert.Single(Executor.Runs).Done.TrySetResult();
            await PumpAsync(published.Task);
            Assert.Equal(ReviewStatus.Ready, (await published.Task).Status);
        }
        finally
        {
            Owner.SnapshotChanged -= Capture;
        }
    }

    private ReviewRevalidationEvidence Evidence(long occurrenceId)
    {
        var group = Assert.Single(Request.Scan.Groups);
        var member = group.Members.Single(value => value.Token.OccurrenceId == occurrenceId);
        var request = new ReviewRevalidationRequest(Owner.Current.Pin!,
            new(Request.Scan.Pin!, Request.Scope.Preflight!.Scope!), group.GroupId,
            group.GroupRevision, member, 1);
        var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value, member.Content!.ByteLength,
            member.ResolvedPath, null, member.Allocation);
        return new(request, facts, facts, new(member.Content.ByteLength, member.Content.Sha256));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var run in Executor.Runs) run.Done.TrySetResult();
        await PumpAsync(Owner.DisposeAsync().AsTask());
    }
}

internal class RecordingOverrideEvidenceStore : IOverrideEvidenceStore
{
    internal ConcurrentQueue<DeliberateOverrideEvidence> Evidence { get; } = new();
    internal OverridePersistOutcome Outcome { get; init; } = OverridePersistOutcome.Persisted;
    internal Func<DeliberateOverrideEvidence, CancellationToken, ValueTask<OverridePersistResult>>? Persist { get; init; }

    public ValueTask<OverridePersistResult> PersistAsync(
        DeliberateOverrideEvidence evidence,
        CancellationToken cancellationToken)
    {
        Evidence.Enqueue(evidence);
        return Persist?.Invoke(evidence, cancellationToken)
            ?? ValueTask.FromResult(new OverridePersistResult(Outcome));
    }

    public ValueTask<OverrideReadResult> ReadAsync(
        OverrideEvidenceId evidenceId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OverrideReadResult(OverrideReadOutcome.Missing, null));
}

internal sealed class DelayedOverrideEvidenceStore : RecordingOverrideEvidenceStore
{
    private readonly TaskCompletionSource<OverridePersistResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DelayedOverrideEvidenceStore()
    {
        Persist = DelayAsync;
    }

    internal TaskCompletionSource<DeliberateOverrideEvidence> PersistEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Complete(OverridePersistOutcome outcome) => completion.TrySetResult(new(outcome));

    private ValueTask<OverridePersistResult> DelayAsync(
        DeliberateOverrideEvidence evidence,
        CancellationToken cancellationToken)
    {
        PersistEntered.TrySetResult(evidence);
        return new(completion.Task);
    }
}
