using System.Collections.Immutable;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Application.Tests.ActionExecution;

public sealed class ActionDurabilityTests
{
    [Fact]
    public async Task SharedOverrideIsReadOnceForTheExactRequestBeforeClaimAndIntent()
    {
        await using var fixture = new ActionFixture(2) { GateAt = "claim" };
        var id = new OverrideEvidenceId(Guid.NewGuid());
        var request = fixture.Request with
        { Items = fixture.Request.Items.Select(item => item with { OverrideEvidenceId = id }).ToImmutableArray() };
        var evidence = new DeliberateOverrideEvidence(id, DateTimeOffset.UnixEpoch,
            new(new(request.ReviewSessionId, request.Scan), request.Items[0].GroupId, 1, 1),
            [1, 2], request.Items.Select(item => new AffectedPhysicalRevision(item.PhysicalFileKey, item.Content, item.Allocation)).ToImmutableArray(),
            "survivor-v1", "survivor-consequence-v1", true);
        fixture.EvidenceRead = new(OverrideReadOutcome.Found, evidence);
        var pending = fixture.Owner.ApproveAsync(request, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(fixture.Entered.Task);
        Assert.Equal(["override", "claim"], fixture.Calls);
        Assert.Same(request, fixture.ClaimedRequest);
        Assert.Same(evidence, Assert.Single(fixture.ClaimedReads).Evidence);
        Assert.Empty(fixture.RequestedFacts);
        fixture.Release.TrySetResult(true);
        await fixture.PumpAsync(pending);
        Assert.Equal(ActionCommandOutcome.Completed, (await pending).Outcome);
        Assert.Equal(1, fixture.Calls.Count(call => call == "override"));
        Assert.All(fixture.RequestedFacts, fact => Assert.Same(evidence, fact.OverrideEvidence));
    }

    [Fact]
    public async Task FreshRejectionAfterAnOutcomeCreatesNoIntentAndIndependentItemReceivesOccurrenceScopedFacts()
    {
        await using var fixture = new ActionFixture(3, independentLast: true) { RejectAt = "refresh:2" };
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionItemState.NotRequested, result.Snapshot.Items[1].State);
        Assert.Equal(ActionSafetyReason.Missing, result.Snapshot.Items[1].Reason);
        Assert.Equal([1L, 3L], fixture.RequestedFacts.Select(fact => fact.Safety.Item.Occurrence.OccurrenceId));
        var preceding = fixture.RequestedFacts[1].Safety.PrecedingOutcomes;
        Assert.Equal([1L, 2L], preceding.Select(value => value.OccurrenceId));
        Assert.Equal([PriorActionDisposition.Recycled, PriorActionDisposition.NotRequested], preceding.Select(value => value.Disposition));
        Assert.All(preceding, value => Assert.Equal(fixture.Request.Items[0].GroupId, value.GroupId));
    }

    // Removing any await between effect receipt and the next effect makes this
    // test observe an early shell call, publication, or second-item refresh.
    [Fact]
    public async Task BothNewFullFactsAndTerminalMustAcknowledgeBeforeAdvancing()
    {
        await using var fixture = new ActionFixture(2);
        var phases = new[] { "claim", "refresh:1", "requested:1", "executing:1", "execute:1", "terminal:1", "refresh:2" };
        var entered = phases.ToDictionary(phase => phase, _ => ScanTestData.Signal<bool>());
        var release = phases.ToDictionary(phase => phase, _ => ScanTestData.Signal<bool>());
        fixture.OnCall = async (phase, token) =>
        {
            if (!entered.TryGetValue(phase, out var signal)) return;
            if (phase.StartsWith("requested:", StringComparison.Ordinal)
                || phase.StartsWith("executing:", StringComparison.Ordinal)
                || phase.StartsWith("terminal:", StringComparison.Ordinal)) Assert.False(token.CanBeCanceled);
            signal.TrySetResult(true);
            await release[phase].Task;
        };
        var pending = fixture.Owner.ApproveAsync(fixture.Request, TestContext.Current.CancellationToken);
        try
        {
            for (var index = 0; index < phases.Length; index++)
            {
                var phase = phases[index];
                await fixture.PumpAsync(entered[phase].Task);
                Assert.Equal(phases.Take(index + 1), fixture.Calls);
                Assert.False(pending.IsCompleted);
                var row = fixture.Owner.Current.Items[0];
                Assert.Equal(index < 3 ? ActionItemState.Waiting : index == 3 ? ActionItemState.Requested
                    : index < 6 ? ActionItemState.Executing : ActionItemState.Recycled, row.State);
                if (phase == "requested:1") Assert.Same(fixture.LastSafety, Assert.Single(fixture.RequestedFacts).Safety);
                if (phase == "execute:1")
                    Assert.Equal(Assert.Single(fixture.RequestedFacts).ActionItemId, Assert.Single(fixture.ExecutingFacts).ActionItemId);
                if (phase == "terminal:1")
                    Assert.Same(ActionFixture.Recycled.Evidence, Assert.Single(fixture.TerminalFacts).Evidence);
                release[phase].TrySetResult(true);
            }
            await fixture.PumpAsync(pending);
            Assert.Equal(ActionCommandOutcome.Completed, (await pending).Outcome);
            Assert.Equal(PriorActionDisposition.Recycled, Assert.Single(fixture.RequestedFacts[1].Safety.PrecedingOutcomes).Disposition);
        }
        finally
        {
            foreach (var signal in release.Values) signal.TrySetResult(true);
        }
    }

    [Theory]
    [InlineData("requested:1", ActionJournalWriteOutcome.Conflict, ActionItemState.NotRequested)]
    [InlineData("requested:1", ActionJournalWriteOutcome.InvalidPredecessor, ActionItemState.NotRequested)]
    [InlineData("requested:1", ActionJournalWriteOutcome.ExactRetry, ActionItemState.InterruptedBeforeExecution)]
    [InlineData("requested:1", ActionJournalWriteOutcome.Unavailable, ActionItemState.NotRequested)]
    [InlineData("executing:1", ActionJournalWriteOutcome.Conflict, ActionItemState.InterruptedBeforeExecution)]
    [InlineData("executing:1", ActionJournalWriteOutcome.InvalidPredecessor, ActionItemState.InterruptedBeforeExecution)]
    [InlineData("executing:1", ActionJournalWriteOutcome.ExactRetry, ActionItemState.UnknownPendingRevalidation)]
    [InlineData("executing:1", ActionJournalWriteOutcome.Unavailable, ActionItemState.InterruptedBeforeExecution)]
    [InlineData("terminal:1", ActionJournalWriteOutcome.Conflict, ActionItemState.UnknownPendingRevalidation)]
    [InlineData("terminal:1", ActionJournalWriteOutcome.InvalidPredecessor, ActionItemState.UnknownPendingRevalidation)]
    [InlineData("terminal:1", ActionJournalWriteOutcome.ExactRetry, ActionItemState.Recycled)]
    [InlineData("terminal:1", ActionJournalWriteOutcome.Unavailable, ActionItemState.UnknownPendingRevalidation)]
    public async Task AnyNonNewReceiptStopsEveryLaterMutationAndNeverReplays(string phase,
        ActionJournalWriteOutcome receipt, ActionItemState state)
    {
        await using var fixture = new ActionFixture(3, independentLast: true)
        { JournalFailureAt = phase, JournalFailure = receipt };
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
        Assert.Equal(state, result.Snapshot.Items[0].State);
        Assert.All(result.Snapshot.Items.Skip(1), item => Assert.Equal(ActionItemState.NotRequested, item.State));
        Assert.Equal(receipt == ActionJournalWriteOutcome.Unavailable ? ActionSafetyReason.JournalUnavailable
            : ActionSafetyReason.JournalConflict, result.Snapshot.FaultReason);
        Assert.Equal(phase == "terminal:1" ? 1 : 0, fixture.Calls.Count(call => call.StartsWith("execute:", StringComparison.Ordinal)));
        var calls = fixture.Calls.ToArray();
        Assert.Equal(ActionCommandOutcome.Existing, (await fixture.ApproveAsync()).Outcome);
        Assert.Equal(calls, fixture.Calls);
    }

    [Theory]
    [InlineData(RecycleBinOutcomeKind.Recycled, ActionItemState.Recycled)]
    [InlineData(RecycleBinOutcomeKind.NotRemoved, ActionItemState.NotRemoved)]
    [InlineData(RecycleBinOutcomeKind.UnknownPendingRevalidation, ActionItemState.UnknownPendingRevalidation)]
    public async Task TerminalExactRetryRetainsExactTruthButNeverAuthorizesTheNextItem(
        RecycleBinOutcomeKind kind, ActionItemState expected)
    {
        await using var fixture = new ActionFixture(2, independentLast: true)
        { JournalFailureAt = "terminal:1", JournalFailure = ActionJournalWriteOutcome.ExactRetry };
        fixture.ResultFor = _ => kind switch
        {
            RecycleBinOutcomeKind.Recycled => ActionFixture.Recycled,
            RecycleBinOutcomeKind.NotRemoved => ActionFixture.NotRemoved,
            _ => ActionFixture.Unknown,
        };
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
        Assert.Equal(expected, result.Snapshot.Items[0].State);
        Assert.Equal(ActionSafetyReason.JournalConflict, result.Snapshot.FaultReason);
        Assert.DoesNotContain("refresh:2", fixture.Calls);
        Assert.DoesNotContain("execute:2", fixture.Calls);
    }

    [Theory]
    [InlineData(ActionItemState.Requested, ActionItemState.InterruptedBeforeExecution)]
    [InlineData(ActionItemState.Executing, ActionItemState.UnknownPendingRevalidation)]
    public async Task CancellationInCommittedPublicationLeavesTheExactOrphanWithoutSyntheticTerminal(
        ActionItemState boundary, ActionItemState expected)
    {
        await using var fixture = new ActionFixture(2);
        using var cancellation = new CancellationTokenSource();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Items[0].State == boundary) cancellation.Cancel();
        };
        var pending = fixture.Owner.ApproveAsync(fixture.Request, cancellation.Token);
        await fixture.PumpAsync(pending);
        Assert.Equal(expected, (await pending).Snapshot.Items[0].State);
        Assert.Empty(fixture.TerminalFacts);
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("execute:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claim", ActionItemState.NotRequested)]
    [InlineData("refresh:1", ActionItemState.NotRequested)]
    [InlineData("requested:1", ActionItemState.NotRequested)]
    [InlineData("executing:1", ActionItemState.InterruptedBeforeExecution)]
    [InlineData("execute:1", ActionItemState.UnknownPendingRevalidation)]
    [InlineData("terminal:1", ActionItemState.UnknownPendingRevalidation)]
    public async Task InterruptedEffectDeliveryCannotInventDurabilityOrReplay(string phase, ActionItemState expected)
    {
        await using var fixture = new ActionFixture(2);
        fixture.OnCall = (at, _) => at == phase ? Task.FromException(new OperationCanceledException("Interrupted effect")) : Task.CompletedTask;
        var result = await fixture.ApproveAsync();
        Assert.Equal(expected, result.Snapshot.Items[0].State);
        Assert.DoesNotContain("execute:2", fixture.Calls);
        Assert.Equal(phase == "execute:1" || phase == "terminal:1" ? 1 : 0, fixture.TerminalFacts.Count);
    }
}
