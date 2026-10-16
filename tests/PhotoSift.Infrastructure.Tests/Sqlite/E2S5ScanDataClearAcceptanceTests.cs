using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Action;
using PhotoSift.Application.LocalData;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class E2S5ScanDataClearAcceptanceTests
{
    [Fact]
    public async Task ConfirmedClearDeletesOnlyScanRowsAndLeavesResolvedActionEvidenceAndSourceBytes()
    {
        await using var fixture = new ScanIdentityFixture();
        var sourcePath = Path.Combine(fixture.RootPath, "source-photo.jpg");
        var sourceBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, TestContext.Current.CancellationToken);
        var beforeHash = SHA256.HashData(sourceBytes);
        var scan = ScanIdentityData.Scan(selectedPath: fixture.RootPath, resolvedPath: fixture.RootPath);
        var requested = JournalData.Requested();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await SeedScanAsync(stores, scan);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistExecutingAsync(
                JournalData.Executing(requested), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistTerminalAsync(
                JournalData.Terminal(requested), TestContext.Current.CancellationToken)).Outcome);

        var result = await stores.ScanData.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScanDataClearOutcome.Cleared, result.Outcome);
        Assert.Equal(1, result.ClearedScanCount);
        Assert.Equal<long?>(0, result.RetainedScanCount);
        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal("0|0|0|0|0", await ScalarAsync(verify, """
            SELECT
              (SELECT COUNT(*) FROM scan_accounting) || '|' ||
              (SELECT COUNT(*) FROM scan_stage_checkpoints) || '|' ||
              (SELECT COUNT(*) FROM scan_lifecycle) || '|' ||
              (SELECT COUNT(*) FROM scan_file_occurrences) || '|' ||
              (SELECT COUNT(*) FROM scans);
            """));
        Assert.Equal("1|2|0", await ScalarAsync(verify, """
            SELECT
              (SELECT COUNT(*) FROM action_items) || '|' ||
              (SELECT COUNT(*) FROM action_transitions) || '|' ||
              (SELECT COUNT(*) FROM action_revalidations);
            """));
        Assert.Equal(beforeHash, SHA256.HashData(
            await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken)));
    }

    public static TheoryData<ActionBoundary> BlockingActionBoundaries => new()
    {
        ActionBoundary.Requested,
        ActionBoundary.Executing,
        ActionBoundary.Unknown,
    };

    [Theory]
    [MemberData(nameof(BlockingActionBoundaries))]
    public async Task AnyUnfinishedOrUnknownActionBlocksTheWholeClearAndExplainsReconciliation(
        ActionBoundary boundary)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        var requested = JournalData.Requested();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await SeedScanAsync(stores, scan);
        await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
        if (boundary is ActionBoundary.Executing or ActionBoundary.Unknown)
            await stores.ActionJournal.PersistExecutingAsync(
                JournalData.Executing(requested), TestContext.Current.CancellationToken);
        if (boundary == ActionBoundary.Unknown)
            await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(requested) with
            {
                Kind = ActionTerminalKind.UnknownPendingRevalidation,
                Reason = PhotoSift.Domain.Action.ActionSafetyReason.Interrupted,
            }, TestContext.Current.CancellationToken);

        var result = await stores.ScanData.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScanDataClearOutcome.BlockedByActionReconciliation, result.Outcome);
        Assert.Equal(0, result.ClearedScanCount);
        Assert.Equal<long?>(1, result.RetainedScanCount);
        Assert.Equal(
            "Local Scan data was retained because an Action is unfinished or unknown. Reconcile the Action record, then try again.",
            result.Message);
        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM scans;"));
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
    }

    [Fact]
    public async Task DatabaseFailureAfterChildDeletesRollsBackEveryScanRowAndReportsRetention()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        var observer = new SqliteDatabaseObserver(AfterScanDataChildrenDeleted: (_, _, _) =>
            ValueTask.FromException(new IOException("Injected clear failure before parent deletes.")));
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath, observer);
        await SeedScanAsync(stores, scan);

        var result = await stores.ScanData.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScanDataClearOutcome.RetainedDatabaseFailure, result.Outcome);
        Assert.Equal(0, result.ClearedScanCount);
        Assert.Equal<long?>(1, result.RetainedScanCount);
        Assert.Equal(
            "Local Scan data could not be cleared and was retained. Check the local database, then try again.",
            result.Message);
        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal("1|2|1|1|1", await ScalarAsync(verify, """
            SELECT
              (SELECT COUNT(*) FROM scan_accounting) || '|' ||
              (SELECT COUNT(*) FROM scan_stage_checkpoints) || '|' ||
              (SELECT COUNT(*) FROM scan_lifecycle) || '|' ||
              (SELECT COUNT(*) FROM scan_file_occurrences) || '|' ||
              (SELECT COUNT(*) FROM scans);
            """));
    }

    [Fact]
    public async Task SchemaFailureBeforeScanCountReportsRetentionAsUnknown()
    {
        await using var fixture = new ScanIdentityFixture();
        await using (var incompatible = await fixture.OpenRawAsync(SqliteOpenMode.ReadWriteCreate))
            await ExecuteAsync(incompatible, "PRAGMA user_version=99;");
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);

        var result = await stores.ScanData.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScanDataClearOutcome.RetainedDatabaseFailure, result.Outcome);
        Assert.Equal(0, result.ClearedScanCount);
        long? retainedScanCount = result.RetainedScanCount;
        Assert.False(retainedScanCount.HasValue);
        Assert.Equal(
            "Local Scan data could not be cleared and was retained. Check the local database, then try again.",
            result.Message);
    }

    [Fact]
    public async Task ClearSharesTheDatabaseOwnersSerializedWriteLane()
    {
        await using var fixture = new ScanIdentityFixture();
        var clearEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneEntries = 0;
        var clearArmed = false;
        var observer = new SqliteDatabaseObserver(
            LaneWaitStarted: laneWasFree =>
            {
                if (Volatile.Read(ref clearArmed) && !laneWasFree) actionQueued.TrySetResult();
            },
            LaneEntered: async _ =>
            {
                if (Volatile.Read(ref clearArmed) && Interlocked.Increment(ref laneEntries) == 1)
                {
                    clearEntered.TrySetResult();
                    await releaseClear.Task;
                }
            });
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath, observer);
        await SeedScanAsync(stores, ScanIdentityData.Scan());
        Volatile.Write(ref clearArmed, true);

        var clear = stores.ScanData.ClearAsync(TestContext.Current.CancellationToken).AsTask();
        await clearEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var action = stores.ActionJournal.PersistRequestedAsync(
            JournalData.Requested(), TestContext.Current.CancellationToken).AsTask();
        await actionQueued.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(action.IsCompleted);
        releaseClear.TrySetResult();
        Assert.Equal(ScanDataClearOutcome.Cleared, (await clear).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await action).Outcome);
    }

    private static async Task SeedScanAsync(SqlitePhotoSiftStores stores, ScanIdentityRecord scan)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 7);
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, Transition(1), ScanLifecycleState.Preflight), token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, Transition(2), ScanLifecycleState.Enumerating), token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(new(
                occurrence,
                new(scan.Pin.ScanId, 7, ScanStage.Enumeration, 1, Transition(3),
                    new(ScanStageState.Succeeded, ScanReason.None), null),
                new(scan.Pin.ScanId, 7, ScanStage.Exact, 1, Transition(3),
                    new(ScanStageState.Succeeded, ScanReason.None),
                    new(ScanStage.Enumeration, new(ScanStageState.Succeeded, ScanReason.None)))), token)).Outcome);
    }

    private static Guid Transition(long suffix) =>
        Guid.Parse($"00000000-0000-4000-8000-{suffix:000000000000}");

    public enum ActionBoundary
    {
        Requested,
        Executing,
        Unknown,
    }
}
