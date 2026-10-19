using System.Collections.Immutable;
using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.ActionExecution;

public sealed class ActionCoordinatorTests
{
    [Fact]
    public async Task RequestBoundObserverFaultCannotStopANewerAdmittedAction()
    {
        await using var fixture = new ActionFixture(1);
        await fixture.ApproveAsync();
        var earlierRequest = fixture.Request.RequestId;
        var next = fixture.Request with { RequestId = Guid.NewGuid() };
        fixture.GateAt = "claim";
        var pending = fixture.Owner.ApproveAsync(next, TestContext.Current.CancellationToken);
        try
        {
            await fixture.PumpAsync(fixture.Entered.Task);
            var report = typeof(IActionCoordinator).GetMethod("ReportObserverFaultAsync", [typeof(Guid)]);
            Assert.NotNull(report);
            var stale = Assert.IsAssignableFrom<Task>(report.Invoke(fixture.Owner, [earlierRequest]));
            await fixture.PumpAsync(stale);
            Assert.Equal(next.RequestId, fixture.Owner.Current.RequestId);
            Assert.Equal(ActionStatus.Claiming, fixture.Owner.Current.Status);
            Assert.Equal(ActionSafetyReason.None, fixture.Owner.Current.FaultReason);
            fixture.Release.TrySetResult(true);
            await fixture.PumpAsync(pending);
            Assert.Equal(ActionCommandOutcome.Completed, (await pending).Outcome);
        }
        finally { fixture.Release.TrySetResult(true); await fixture.PumpAsync(pending); }
    }

    [Fact]
    public async Task RequestBoundObserverFaultStopsItsOriginatingActionBeforeAnyIntent()
    {
        await using var fixture = new ActionFixture(1) { GateAt = "claim" };
        var pending = fixture.Owner.ApproveAsync(fixture.Request, TestContext.Current.CancellationToken);
        try
        {
            await fixture.PumpAsync(fixture.Entered.Task);
            var report = typeof(IActionCoordinator).GetMethod("ReportObserverFaultAsync", [typeof(Guid)]);
            Assert.NotNull(report);
            var fault = Assert.IsAssignableFrom<Task>(report.Invoke(fixture.Owner, [fixture.Request.RequestId]));
            await fixture.PumpAsync(fault);
            Assert.Equal(ActionStatus.Faulted, fixture.Owner.Current.Status);
            Assert.Equal(ActionSafetyReason.ProgrammingFault, fixture.Owner.Current.FaultReason);
            fixture.Release.TrySetResult(true);
            await fixture.PumpAsync(pending);
            Assert.Empty(fixture.RequestedFacts);
            Assert.DoesNotContain("execute:1", fixture.Calls);
        }
        finally { fixture.Release.TrySetResult(true); await fixture.PumpAsync(pending); }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("default")]
    [InlineData("id")]
    [InlineData("order")]
    [InlineData("selection")]
    [InlineData("duplicate")]
    [InlineData("pin")]
    public async Task MalformedRequestsNeverReachAuthority(string mutation)
    {
        await using var fixture = new ActionFixture(2);
        var request = fixture.Request;
        request = mutation switch
        {
            "empty" => request with { Items = [] },
            "default" => request with { Items = default },
            "id" => request with { RequestId = Guid.Empty },
            "order" => request with { Items = [.. request.Items.Reverse()] },
            "selection" => request with { SelectedOccurrenceIds = [99] },
            "duplicate" => request with { Items = [request.Items[0], request.Items[0]] },
            _ => request with { Items = request.Items.SetItem(0, request.Items[0] with
                { Occurrence = request.Items[0].Occurrence with { Pin = request.Scan with { ScopeRevision = 99 } } }) },
        };
        var result = await fixture.ApproveAsync(request);
        Assert.Equal(ActionCommandOutcome.Rejected, result.Outcome);
        Assert.Empty(fixture.Calls);
        Assert.Equal(ActionStatus.Idle, fixture.Owner.Current.Status);
    }

    [Fact]
    public async Task RepeatedAndConflictingRequestsReserveBeforeAuthorityAndNeverReplay()
    {
        await using var fixture = new ActionFixture(2) { GateAt = "claim" };
        var first = fixture.Owner.ApproveAsync(fixture.Request, TestContext.Current.CancellationToken);
        await fixture.PumpAsync(fixture.Entered.Task);
        var copy = fixture.Request with { Items = [.. fixture.Request.Items], SelectedOccurrenceIds = [.. fixture.Request.SelectedOccurrenceIds] };
        Assert.Equal(ActionCommandOutcome.Existing, (await fixture.ApproveAsync(copy)).Outcome);
        Assert.Equal(ActionCommandOutcome.Rejected, (await fixture.ApproveAsync(fixture.Request with
            { Disclosure = fixture.Request.Disclosure with { SelectedOccurrenceCount = 99 } })).Outcome);
        Assert.Equal(["claim"], fixture.Calls);
        fixture.Release.TrySetResult(true);
        await fixture.PumpAsync(first);
        Assert.Equal(ActionCommandOutcome.Completed, (await first).Outcome);
        Assert.Equal(ActionCommandOutcome.Existing, (await fixture.ApproveAsync(copy)).Outcome);
        Assert.Equal(["claim", "refresh:1", "requested:1", "executing:1", "execute:1", "terminal:1",
            "refresh:2", "requested:2", "executing:2", "execute:2", "terminal:2"], fixture.Calls);
        Assert.Same(fixture.Request, fixture.ClaimedRequest);
        Assert.All(fixture.RequestedFacts, fact => Assert.Equal(64, fact.RequestPayloadSha256.Length));
        Assert.Single(fixture.RequestedFacts.Select(fact => fact.RequestPayloadSha256).Distinct());
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("refresh:1")]
    public async Task StaleAuthorityOrUnsafeRefreshCannotBecomeIntent(string phase)
    {
        await using var fixture = new ActionFixture(1) { RejectAt = phase };
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionCommandOutcome.Incomplete, result.Outcome);
        Assert.Equal(ActionItemState.NotRequested, result.Snapshot.Items[0].State);
        Assert.Equal(0, result.Snapshot.DurableRequestedCount);
        Assert.Empty(fixture.RequestedFacts);
    }

    [Theory]
    [InlineData("requested:1", ActionJournalWriteOutcome.Unavailable, ActionItemState.NotRequested, 0, 0)]
    [InlineData("requested:1", ActionJournalWriteOutcome.ExactRetry, ActionItemState.InterruptedBeforeExecution, 1, 0)]
    [InlineData("executing:1", ActionJournalWriteOutcome.Conflict, ActionItemState.InterruptedBeforeExecution, 1, 0)]
    [InlineData("executing:1", ActionJournalWriteOutcome.ExactRetry, ActionItemState.UnknownPendingRevalidation, 1, 1)]
    [InlineData("terminal:1", ActionJournalWriteOutcome.Unavailable, ActionItemState.UnknownPendingRevalidation, 1, 1)]
    public async Task JournalFailureOrExistingIntentNeverAuthorizesReplay(string phase,
        ActionJournalWriteOutcome outcome, ActionItemState expected, long requested, long executing)
    {
        await using var fixture = new ActionFixture(2) { JournalFailureAt = phase, JournalFailure = outcome };
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
        Assert.Equal(expected, result.Snapshot.Items[0].State);
        Assert.Equal(ActionItemState.NotRequested, result.Snapshot.Items[1].State);
        Assert.Equal(requested, result.Snapshot.DurableRequestedCount);
        Assert.Equal(executing, result.Snapshot.DurableExecutingCount);
        Assert.Equal(phase == "terminal:1" ? 1 : 0, fixture.Calls.Count(call => call.StartsWith("execute:", StringComparison.Ordinal)));
        Assert.Equal(ActionCommandOutcome.Existing, (await fixture.ApproveAsync()).Outcome);
    }

    [Fact]
    public async Task UnknownBlocksDependentGroupButIndependentGroupStillRefreshesAndRuns()
    {
        await using var fixture = new ActionFixture(3, independentLast: true);
        fixture.ResultFor = id => id == 1 ? ActionFixture.Unknown : ActionFixture.Recycled;
        var result = await fixture.ApproveAsync();
        Assert.Equal(ActionCommandOutcome.Incomplete, result.Outcome);
        Assert.Equal([ActionItemState.UnknownPendingRevalidation, ActionItemState.NotRequested, ActionItemState.Recycled],
            result.Snapshot.Items.Select(row => row.State));
        Assert.Equal(ActionSafetyReason.DependentOutcomeUnknown, result.Snapshot.Items[1].Reason);
        Assert.DoesNotContain("execute:2", fixture.Calls);
        Assert.Contains("refresh:3", fixture.Calls);
        Assert.Equal([PriorActionDisposition.Unknown, PriorActionDisposition.NotRequested],
            fixture.LastPreceding.Select(row => row.Disposition));
    }

    [Fact]
    public async Task AffirmedNotRemovedIsPassedToFreshSubsequentSafetyAndCountsComeFromRows()
    {
        await using var fixture = new ActionFixture(2);
        fixture.ResultFor = id => id == 1 ? ActionFixture.NotRemoved : ActionFixture.Recycled;
        var result = await fixture.ApproveAsync();
        Assert.Equal(PriorActionDisposition.AffirmedNotRemoved, Assert.Single(fixture.LastPreceding).Disposition);
        Assert.Equal((2L, 2L, 1L, 1L, 0L), (result.Snapshot.DurableRequestedCount,
            result.Snapshot.DurableExecutingCount, result.Snapshot.RecycledCount, result.Snapshot.NotRemovedCount, result.Snapshot.UnknownCount));
    }

    [Fact]
    public async Task MissingOverrideReadFailsBeforeClaimAndNeverCreatesIntent()
    {
        await using var fixture = new ActionFixture(1);
        var item = fixture.Request.Items[0] with { OverrideEvidenceId = new(Guid.NewGuid()) };
        var result = await fixture.ApproveAsync(fixture.Request with { Items = [item] });
        Assert.Equal(ActionItemState.NotRequested, result.Snapshot.Items[0].State);
        Assert.Equal(ActionSafetyReason.OverrideMissing, result.Snapshot.Items[0].Reason);
        Assert.Equal(["override"], fixture.Calls);
    }
}

internal sealed class ActionFixture : IAsyncDisposable, IApprovedActionAuthority, IActionJournalStore,
    IRecycleBinExecutor, IOverrideEvidenceStore
{
    internal static readonly RecycleBinResult Recycled = new(RecycleBinOutcomeKind.Recycled, ActionSafetyReason.None,
        new(1, 0, true, 0, false, 1, 0, 0, true, false, false));
    internal static readonly RecycleBinResult NotRemoved = new(RecycleBinOutcomeKind.NotRemoved, ActionSafetyReason.CannotRecycle,
        new(1, 0, false, null, null, 0, 0, null, false, false, true));
    internal static readonly RecycleBinResult Unknown = new(RecycleBinOutcomeKind.UnknownPendingRevalidation, ActionSafetyReason.CallbackContradiction,
        new(1, 1, true, 0, false, 1, 0, 0, true, false, false));
    internal ManualScanScheduler Scheduler { get; } = new();
    internal ActionCoordinator Owner { get; }
    internal ApprovedActionRequest Request { get; }
    internal ApprovedActionRequest? ClaimedRequest { get; private set; }
    internal List<string> Calls { get; } = [];
    internal List<ActionRequestedFact> RequestedFacts { get; } = [];
    internal List<ActionExecutingFact> ExecutingFacts { get; } = [];
    internal List<ActionTerminalFact> TerminalFacts { get; } = [];
    internal ActionSafetyEnvelope? LastSafety { get; private set; }
    internal OverrideReadResult EvidenceRead { get; set; } = new(OverrideReadOutcome.Missing, null);
    internal ImmutableArray<OverrideReadResult> ClaimedReads { get; private set; } = [];
    internal ImmutableArray<PriorActionOutcome> LastPreceding { get; private set; } = [];
    internal TaskCompletionSource<bool> Entered { get; } = ScanTestData.Signal<bool>();
    internal TaskCompletionSource<bool> Release { get; } = ScanTestData.Signal<bool>();
    internal TaskCompletionSource<bool> CancellationObserved { get; } = ScanTestData.Signal<bool>();
    internal string? GateAt { get; set; }
    internal string? RejectAt { get; set; }
    internal string? JournalFailureAt { get; set; }
    internal ActionJournalWriteOutcome JournalFailure { get; set; } = ActionJournalWriteOutcome.Unavailable;
    internal Func<long, RecycleBinResult> ResultFor { get; set; } = _ => Recycled;
    internal Func<string, CancellationToken, Task>? OnCall { get; set; }
    internal CancellationToken GatedToken { get; private set; }

    internal ActionFixture(int count, bool independentLast = false)
    {
        Request = RequestFor(count, independentLast);
        Owner = new(Scheduler, this, this, this, this, resultCapacity: 1);
    }
    internal static ApprovedActionRequest RequestFor(int count, bool independentLast = false)
    {
        var context = ScanTestData.Context();
        var group = Guid.NewGuid();
        var items = Enumerable.Range(1, count).Select(id =>
        {
            var work = ScanTestData.Work(id, context);
            return new ApprovedActionItem(independentLast && id == count ? Guid.NewGuid() : group, 1,
                work.Token, work.File.DiscoveredPath, work.File.ResolvedPath!, work.File.PhysicalFileKey!.Value,
                ScanTestData.Revision(work), new(4096, 1, true), null);
        }).ToImmutableArray();
        return new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.NewGuid(), 1, context.Pin, context.Scope,
            items, items.Select(item => item.Occurrence.OccurrenceId).ToImmutableArray(),
            new(count, count, ScanStatus.Complete, new(0, 0, 0, 0, 0, 0, 0),
                new(4096 * count, SelectionRecoveryUnavailableReason.None), "Windows Recycle Bin", "Revalidate"));
    }
    internal Task PumpAsync(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));
    internal async Task<ActionCommandResult> ApproveAsync(ApprovedActionRequest? request = null)
    {
        var pending = Owner.ApproveAsync(request ?? Request, TestContext.Current.CancellationToken);
        await PumpAsync(pending);
        return await pending;
    }
    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult(true);
        await PumpAsync(Owner.DisposeAsync().AsTask());
    }
    private async Task CallAsync(string phase, CancellationToken token)
    {
        Calls.Add(phase);
        if (OnCall is { } callback) await callback(phase, token);
        if (phase != GateAt) return;
        GatedToken = token;
        using var registration = token.Register(() => CancellationObserved.TrySetResult(true));
        Entered.TrySetResult(true);
        await Release.Task;
    }
    public async Task<ApprovedActionClaimResult> ClaimAsync(ApprovedActionRequest request,
        ImmutableArray<OverrideReadResult> overrideReads, CancellationToken cancellationToken)
    {
        ClaimedRequest = request;
        ClaimedReads = overrideReads;
        await CallAsync("claim", cancellationToken);
        return RejectAt == "claim" ? new(ApprovedActionClaimOutcome.Stale, null, ActionSafetyReason.StalePin)
            : new(ApprovedActionClaimOutcome.Claimed, new(new(Guid.NewGuid()), request), ActionSafetyReason.None);
    }
    public async Task<ActionSafetyRefreshResult> RefreshSafetyAsync(ApprovedActionClaim claim,
        ApprovedActionItem item, ImmutableArray<PriorActionOutcome> precedingOutcomes, CancellationToken cancellationToken)
    {
        var phase = $"refresh:{item.Occurrence.OccurrenceId}";
        await CallAsync(phase, cancellationToken);
        LastPreceding = precedingOutcomes;
        if (RejectAt == phase) return new(ApprovedActionClaimOutcome.UnsafeCurrentFacts, null, ActionSafetyReason.Missing);
        var selected = claim.Request.Items.Where(value => value.GroupId == item.GroupId).ToArray();
        var members = selected.Select(Occurrence).Append(Occurrence(item with
        {
            Occurrence = item.Occurrence with { OccurrenceId = 100 + item.Occurrence.OccurrenceId },
            DiscoveredPath = $@"C:\Photos\witness{item.Occurrence.OccurrenceId}.jpg",
            ResolvedPath = $@"C:\Photos\witness{item.Occurrence.OccurrenceId}.jpg",
            PhysicalFileKey = new(1, new(0, 100 + (ulong)item.Occurrence.OccurrenceId)),
            Content = new(new(1, new(0, 100 + (ulong)item.Occurrence.OccurrenceId)), 1, ScanTestData.Digest),
        })).ToImmutableArray();
        var group = new CandidateGroup(item.GroupId, item.GroupRevision, claim.Request.Scan,
            CandidateCategory.Exact, members, [], false, null, new(0, RecoveryUnavailableReason.None));
        var recycled = precedingOutcomes.Where(value => value.Disposition == PriorActionDisposition.Recycled)
            .Select(value => value.OccurrenceId).ToImmutableHashSet();
        var current = members.Where(value => !recycled.Contains(value.Token.OccurrenceId)).ToImmutableArray();
        var remaining = selected.Select(value => value.Occurrence.OccurrenceId)
            .Except(precedingOutcomes.Select(value => value.OccurrenceId)).ToImmutableHashSet();
        var input = new ActionSafetyInput(group, current, current.Select(value => value.Token.OccurrenceId).ToImmutableHashSet(),
            remaining, [], false, false);
        var observations = current.Select(value => new ActionProbeObservation(value,
            new(value.PhysicalFileKey!.Value, 1, value.ResolvedPath, null, value.Allocation),
            new(value.PhysicalFileKey!.Value, 1, value.ResolvedPath, null, value.Allocation), new(1, ScanTestData.Digest))).ToImmutableArray();
        LastSafety = new(claim, item, input, ActionSafetyPolicy.Evaluate(input), observations, precedingOutcomes);
        return new(ApprovedActionClaimOutcome.Claimed, LastSafety, ActionSafetyReason.None);
    }
    private static ScanFileOccurrence Occurrence(ApprovedActionItem item) => new(item.Occurrence,
        item.DiscoveredPath, item.ResolvedPath, item.PhysicalFileKey, ScanTestData.Success,
        ScanTestData.Success, item.Content, item.Allocation);
    public async ValueTask<ActionJournalWriteResult> PersistRequestedAsync(ActionRequestedFact fact, CancellationToken cancellationToken)
    {
        RequestedFacts.Add(fact);
        return await WriteAsync($"requested:{fact.Safety.Item.Occurrence.OccurrenceId}", cancellationToken);
    }
    public ValueTask<ActionJournalWriteResult> PersistExecutingAsync(ActionExecutingFact fact, CancellationToken cancellationToken)
    {
        ExecutingFacts.Add(fact);
        return WriteAsync($"executing:{ItemId(fact.ActionItemId)}", cancellationToken);
    }
    public ValueTask<ActionJournalWriteResult> PersistTerminalAsync(ActionTerminalFact fact, CancellationToken cancellationToken)
    {
        TerminalFacts.Add(fact);
        return WriteAsync($"terminal:{ItemId(fact.ActionItemId)}", cancellationToken);
    }
    private long ItemId(Guid actionItemId) => RequestedFacts.Single(fact => fact.ActionItemId == actionItemId).Safety.Item.Occurrence.OccurrenceId;
    private async ValueTask<ActionJournalWriteResult> WriteAsync(string phase, CancellationToken cancellationToken)
    {
        await CallAsync(phase, cancellationToken);
        return new(phase == JournalFailureAt ? JournalFailure : ActionJournalWriteOutcome.Persisted);
    }
    public async ValueTask<RecycleBinResult> ExecuteAsync(RecycleBinRequest request, CancellationToken cancellationToken)
    {
        await CallAsync($"execute:{request.Safety.Item.Occurrence.OccurrenceId}", cancellationToken);
        return ResultFor(request.Safety.Item.Occurrence.OccurrenceId);
    }
    public ValueTask<OverridePersistResult> PersistAsync(DeliberateOverrideEvidence evidence, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Action must not write Review evidence.");
    public ValueTask<OverrideReadResult> ReadAsync(OverrideEvidenceId evidenceId, CancellationToken cancellationToken)
    {
        Calls.Add("override");
        return ValueTask.FromResult(EvidenceRead);
    }
}
