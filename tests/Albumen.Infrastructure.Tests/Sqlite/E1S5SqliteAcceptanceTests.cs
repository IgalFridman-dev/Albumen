using System.Security.Cryptography;
using System.Text;
using Albumen.Application.Action;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Tests.ActionExecution;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

[Collection(WindowsRecycleBinIsolationDefinition.Name)]
[Trait("Category", "GoldenVm")]
public sealed class E1S5SqliteAcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalCrashOrReadbackContradictionRetainsExecutingAcrossStoreReopenAndStopsTheSealedRemainder(bool corruptReadback)
    {
        var inserts = 0;
        var fullChecks = 0;
        var observer = new SqliteDatabaseObserver(BeforeActionTransaction: async (connection, token) =>
        {
            Assert.False(token.CanBeCanceled);
            Assert.Equal(2L, await ScalarAsync(connection, "PRAGMA synchronous;"));
            Interlocked.Increment(ref fullChecks);
        }, AfterActionInsert: async (connection, transaction, token) =>
        {
            Assert.False(token.CanBeCanceled);
            if (Interlocked.Increment(ref inserts) != 3) return;
            if (!corruptReadback) throw new IOException("Disposable terminal pre-commit interruption");
            await ExecuteAsync(connection,
                "UPDATE action_transitions SET transition_payload='{}' WHERE sequence=3;", transaction);
        });
        await using var fixture = new RealActionAcceptanceFixture(observer, executor: new RetainedExecutor());
        var request = await fixture.ApproveReviewAsync(1, 2);
        var result = await RealActionAcceptanceFixture.Bound(fixture.Action.ApproveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
        Assert.Equal(ActionSafetyReason.JournalUnavailable, result.Snapshot.FaultReason);
        Assert.Equal([ActionItemState.UnknownPendingRevalidation, ActionItemState.NotRequested], result.Snapshot.Items.Select(item => item.State));
        Assert.Equal(1L, result.Snapshot.DurableRequestedCount);
        Assert.Equal(1L, result.Snapshot.DurableExecutingCount);
        Assert.Equal(0L, result.Snapshot.NotRemovedCount);
        Assert.Equal(1, fixture.Executor.Executions);
        Assert.Equal(RecycleBinOutcomeKind.NotRemoved, fixture.Executor.Result!.Outcome);
        Assert.Equal(3, fullChecks);

        string requestedPayload;
        string executingPayload;
        await using (var read = await fixture.OpenRawAsync())
        {
            Assert.Equal(5L, await ScalarAsync(read, "PRAGMA user_version;"));
            Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_items;"));
            Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions;"));
            Assert.Equal("executing", await ScalarAsync(read, "SELECT transition_kind FROM action_transitions;"));
            requestedPayload = Assert.IsType<string>(await ScalarAsync(read, "SELECT requested_payload FROM action_items;"));
            executingPayload = Assert.IsType<string>(await ScalarAsync(read, "SELECT transition_payload FROM action_transitions;"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestedPayload))),
                await ScalarAsync(read, "SELECT requested_payload_sha256 FROM action_items;"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executingPayload))),
                await ScalarAsync(read, "SELECT transition_payload_sha256 FROM action_transitions;"));
        }
        Assert.Equal(ActionCommandOutcome.Existing,
            (await RealActionAcceptanceFixture.Bound(fixture.Action.ApproveAsync(request, CancellationToken.None))).Outcome);
        await fixture.Action.DisposeAsync();
        await fixture.Stores.DisposeAsync();
        await using (var reopened = new SqliteAlbumenStores(fixture.DatabasePath))
        await using (var freshOwner = new ActionCoordinator(fixture.Scheduler, fixture.Review,
            reopened.OverrideEvidence, reopened.ActionJournal, fixture.Executor))
        {
            // The surviving Review owner must not issue its spent claim again.
            // E1-S5 has no path to reconstruct/reconcile that authority on reopen.
            var replay = await RealActionAcceptanceFixture.Bound(freshOwner.ApproveAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal(ActionCommandOutcome.Incomplete, replay.Outcome);
            Assert.Equal(ActionSafetyReason.StalePin, replay.Snapshot.Items[0].Reason);
            Assert.All(replay.Snapshot.Items, item => Assert.Equal(ActionItemState.NotRequested, item.State));
        }
        Assert.Equal(1, fixture.Executor.Executions);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(requestedPayload, await ScalarAsync(verify, "SELECT requested_payload FROM action_items;"));
        Assert.Equal(executingPayload, await ScalarAsync(verify, "SELECT transition_payload FROM action_transitions;"));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions WHERE sequence=3;"));
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal("fixture", File.ReadAllText(fixture.Selected));
        Assert.Equal("fixture", File.ReadAllText(fixture.Other));
        Assert.Equal("fixture", File.ReadAllText(fixture.Witness));
    }

    private sealed class RetainedExecutor : IRecycleBinExecutor
    {
        public ValueTask<RecycleBinResult> ExecuteAsync(RecycleBinRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RecycleBinResult(RecycleBinOutcomeKind.NotRemoved, ActionSafetyReason.CannotRecycle,
                new(0, 0, false, null, null, 0, 0, null, false, false, true)));
    }
}
