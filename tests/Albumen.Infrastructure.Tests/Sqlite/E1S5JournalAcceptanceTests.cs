using System.Collections.Immutable;
using System.Text.Json;
using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Tests.Exact;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class E1S5JournalAcceptanceTests
{
    [Fact]
    public async Task RealFullCommitsPrecedeExecutorAndTerminalPublication()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var scheduler = new SerialTestScheduler();
        var ports = new AcceptancePorts(fixture);
        var entered = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
        var release = Enumerable.Range(0, 3).Select(_ => Signal()).ToArray();
        var inserts = 0;
        var fullChecks = 0;
        var observer = new SqliteDatabaseObserver(BeforeActionTransaction: async (connection, token) =>
        {
            Assert.False(token.CanBeCanceled);
            Assert.Equal(2L, await ScalarAsync(connection, "PRAGMA synchronous;"));
            await ExecuteAsync(connection, "PRAGMA synchronous=FULL;");
            Interlocked.Increment(ref fullChecks);
        }, AfterActionInsert: async (_, _, token) =>
        {
            Assert.False(token.CanBeCanceled);
            var index = Interlocked.Increment(ref inserts) - 1;
            entered[index].TrySetResult();
            await release[index].Task;
        });
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath, observer);
        await using var owner = new ActionCoordinator(scheduler, ports, stores.OverrideEvidence, stores.ActionJournal, ports);
        var pending = owner.ApproveAsync(ports.Request, TestContext.Current.CancellationToken);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                await Bound(entered[index].Task);
                Assert.Equal(index == 2 ? 1 : 0, ports.Executions);
                Assert.Equal(index == 0 ? ActionItemState.Waiting : index == 1 ? ActionItemState.Requested
                    : ActionItemState.Executing, owner.Current.Items[0].State);
                await using (var read = await fixture.OpenRawAsync())
                {
                    Assert.Equal(index == 0 ? 0L : 1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_items;"));
                    Assert.Equal(index == 2 ? 1L : 0L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions;"));
                }
                release[index].TrySetResult();
            }
            var result = await Bound(pending);
            Assert.Equal(ActionCommandOutcome.Completed, result.Outcome);
            Assert.Equal(ActionItemState.Recycled, result.Snapshot.Items[0].State);
            Assert.Equal(3, fullChecks);
            await using var verify = await fixture.OpenRawAsync();
            Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
            using var payload = JsonDocument.Parse(Assert.IsType<string>(await ScalarAsync(verify, "SELECT requested_payload FROM action_items;")));
            var safety = payload.RootElement.GetProperty("fact").GetProperty("safety");
            Assert.Equal(16384, safety.GetProperty("observations")[0].GetProperty("final").GetProperty("allocation").GetProperty("allocatedBytes").GetInt64());
            Assert.Equal(4, safety.GetProperty("input").GetProperty("currentOccurrences")[0].GetProperty("allocation").GetProperty("linkCount").GetInt32());
        }
        finally { foreach (var signal in release) signal.TrySetResult(); }
    }

    [Theory]
    [InlineData(ActionItemState.Requested, ActionItemState.InterruptedBeforeExecution, 0)]
    [InlineData(ActionItemState.Executing, ActionItemState.UnknownPendingRevalidation, 1)]
    public async Task CommittedOrphanSurvivesCancellationReopenAndFreshCoordinatorWithoutReplay(
        ActionItemState boundary, ActionItemState expected, long transitions)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var scheduler = new SerialTestScheduler();
        var ports = new AcceptancePorts(fixture);
        using var cancellation = new CancellationTokenSource();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        await using (var owner = new ActionCoordinator(scheduler, ports, stores.OverrideEvidence, stores.ActionJournal, ports))
        {
            owner.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.Items[0].State == boundary) cancellation.Cancel();
            };
            var result = await Bound(owner.ApproveAsync(ports.Request, cancellation.Token));
            Assert.Equal(expected, result.Snapshot.Items[0].State);
        }
        await using (var reopened = new SqliteAlbumenStores(fixture.DatabasePath))
        await using (var another = new ActionCoordinator(scheduler, ports, reopened.OverrideEvidence, reopened.ActionJournal, ports))
        {
            var result = await Bound(another.ApproveAsync(ports.Request, TestContext.Current.CancellationToken));
            Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
            Assert.Equal(ActionSafetyReason.JournalConflict, result.Snapshot.FaultReason);
        }
        Assert.Equal(0, ports.Executions);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(transitions, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions WHERE sequence=3;"));
    }

    [Theory]
    [InlineData(1, ActionItemState.NotRequested, 0, 0, 0)]
    [InlineData(2, ActionItemState.InterruptedBeforeExecution, 1, 0, 0)]
    [InlineData(3, ActionItemState.UnknownPendingRevalidation, 1, 1, 1)]
    public async Task RealRollbackAtEachBarrierPreservesOnlyCommittedPredecessors(int failingInsert,
        ActionItemState state, long requested, long transitions, int executions)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var scheduler = new SerialTestScheduler();
        var ports = new AcceptancePorts(fixture);
        var inserts = 0;
        var observer = new SqliteDatabaseObserver(AfterActionInsert: (_, _, _) =>
            ++inserts == failingInsert ? ValueTask.FromException(new IOException("Interrupted before commit")) : ValueTask.CompletedTask);
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath, observer))
        await using (var owner = new ActionCoordinator(scheduler, ports, stores.OverrideEvidence, stores.ActionJournal, ports))
        {
            var result = await Bound(owner.ApproveAsync(ports.Request, TestContext.Current.CancellationToken));
            Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
            Assert.Equal(state, result.Snapshot.Items[0].State);
        }
        Assert.Equal(executions, ports.Executions);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(requested, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(transitions, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    private static Task<T> Bound<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

    private sealed class AcceptancePorts : IApprovedActionAuthority, IRecycleBinExecutor
    {
        private readonly SqliteOverrideFixture fixture;
        private readonly ActionSafetyEnvelope safety;
        internal AcceptancePorts(SqliteOverrideFixture fixture)
        {
            this.fixture = fixture;
            var seed = JournalData.Requested().Safety;
            var item = seed.Item with { OverrideEvidenceId = null };
            Request = seed.Claim.Request with { Items = [item], SelectedOccurrenceIds = [1] };
            var original = seed.Input.CurrentOccurrences[0];
            var current = new Albumen.Domain.Scan.ScanFileOccurrence(original.Token, original.DiscoveredPath,
                original.ResolvedPath, original.PhysicalFileKey, original.Enumerate, original.Exact, original.Content, new(16384, 4, true));
            var input = seed.Input with { CurrentOccurrences = [current], OverrideApprovedOccurrenceIds = [], OverrideEvidenceExact = false };
            var observed = seed.Observations[0];
            safety = seed with
            {
                Claim = seed.Claim with { Request = Request }, Item = item, Input = input,
                Decision = ActionSafetyPolicy.Evaluate(input), PrecedingOutcomes = [],
                Observations = [observed with { Opened = observed.Opened with { Allocation = current.Allocation }, Final = observed.Final with { Allocation = current.Allocation } }],
            };
        }
        internal ApprovedActionRequest Request { get; }
        internal int Executions { get; private set; }
        public Task<ApprovedActionClaimResult> ClaimAsync(ApprovedActionRequest request,
            ImmutableArray<OverrideReadResult> overrideReads, CancellationToken cancellationToken) =>
            Task.FromResult(new ApprovedActionClaimResult(ApprovedActionClaimOutcome.Claimed, safety.Claim, ActionSafetyReason.None));
        public Task<ActionSafetyRefreshResult> RefreshSafetyAsync(ApprovedActionClaim claim, ApprovedActionItem item,
            ImmutableArray<PriorActionOutcome> precedingOutcomes, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionSafetyRefreshResult(ApprovedActionClaimOutcome.Claimed, safety, ActionSafetyReason.None));
        public async ValueTask<RecycleBinResult> ExecuteAsync(RecycleBinRequest request, CancellationToken cancellationToken)
        {
            Executions++;
            Assert.Same(safety, request.Safety);
            await using var read = await fixture.OpenRawAsync();
            Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_items;"));
            Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions WHERE sequence=2;"));
            Assert.Equal(0L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions WHERE sequence=3;"));
            Assert.Equal(request.ActionItemId.ToString("D"), await ScalarAsync(read, "SELECT action_item_id FROM action_transitions;"));
            return new(RecycleBinOutcomeKind.Recycled, ActionSafetyReason.None,
                new(1, 0, true, 0, false, 1, 0, 0, true, false, false));
        }
    }
}
