using Microsoft.Data.Sqlite;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteScanCheckpointStoreTests
{
    private static readonly Guid LifecycleTransitionId =
        Guid.Parse("df95504d-46b4-4a01-b593-cc64801484c0");
    private static readonly Guid EnumerationTransitionId =
        Guid.Parse("be3f6bd3-d7a1-49e3-8d3d-b83b01e26010");
    private static readonly Guid ExactStartTransitionId =
        Guid.Parse("4f46773c-894e-43d2-a66a-2b946d9109c7");
    private static readonly Guid ExactTerminalTransitionId =
        Guid.Parse("5b21b75d-c4d8-43ec-a9a4-95b8e4b1c281");
    private static readonly StageOutcome Succeeded =
        new(ScanStageState.Succeeded, ScanReason.None);
    private static readonly StageOutcome Running =
        new(ScanStageState.Running, ScanReason.None);

    [Fact]
    public async Task ExistingScanBootstrapsCanonicalPreflightLifecycleAndZeroAccounting()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var scan = ScanIdentityData.Scan();
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        var command = new ScanLifecycleTransitionCommand(
            scan.Pin.ScanId, 1, LifecycleTransitionId, ScanLifecycleState.Preflight);

        var accepted = await stores.ScanCheckpoints.TransitionLifecycleAsync(command, token);
        var retry = await stores.ScanCheckpoints.TransitionLifecycleAsync(command, token);

        Assert.Equal(ScanCheckpointWriteOutcome.Accepted, accepted.Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Idempotent, retry.Outcome);
        var expectedLifecycle = new ScanLifecycleCheckpoint(
            scan.Pin.ScanId, 1, LifecycleTransitionId, ScanLifecycleState.Preflight);
        Assert.Equal(expectedLifecycle, accepted.Snapshot!.Lifecycle);
        Assert.Equal(expectedLifecycle, retry.Snapshot!.Lifecycle);
        Assert.Empty(accepted.Snapshot.Stages);
        Assert.Equal(new ScanAccounting(0, 0, 0, 0, 0, 0), accepted.Snapshot.Accounting);
    }

    [Fact]
    public async Task InitialOccurrenceAtomicallyCreatesIdentityAndCanonicalEnumerationExactRows()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var (scan, initial) = await BootstrapAsync(stores);

        var accepted = await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
            initial, TestContext.Current.CancellationToken);
        var retry = await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
            initial, TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointWriteOutcome.Accepted, accepted.Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Idempotent, retry.Outcome);
        Assert.Equal(new ScanAccounting(1, 0, 0, 0, 0, 1), accepted.Snapshot!.Accounting);
        Assert.Equal(
            new[] { initial.Enumeration, initial.Exact },
            accepted.Snapshot.Stages.Select(stage => stage.Stored).ToArray());
        Assert.All(accepted.Snapshot.Stages, stage =>
        {
            Assert.False(stage.RecoveredFromInterruption);
            Assert.Equal(stage.Stored.Outcome, stage.Outcome);
        });
        Assert.Equal([initial.Occurrence], (await stores.ScanIdentities.ReadOccurrencesAsync(
            scan.Pin.ScanId, TestContext.Current.CancellationToken)).Records);
    }

    [Fact]
    public async Task LegalExactTerminalTransitionUpdatesRevisionAndAccountingOnce()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var (_, initial) = await BootstrapAsync(stores);
        await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(initial, TestContext.Current.CancellationToken);
        var command = new ScanStageTransitionCommand(
            initial.Occurrence.Token.Pin.ScanId,
            initial.Occurrence.Token.OccurrenceId,
            ScanStage.Exact,
            1,
            ExactTerminalTransitionId,
            Succeeded);

        var accepted = await stores.ScanCheckpoints.TransitionStageAsync(
            command, TestContext.Current.CancellationToken);
        var retry = await stores.ScanCheckpoints.TransitionStageAsync(
            command, TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointWriteOutcome.Accepted, accepted.Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Idempotent, retry.Outcome);
        var exact = Assert.Single(accepted.Snapshot!.Stages,
            stage => stage.Stored.Stage == ScanStage.Exact);
        Assert.Equal(2, exact.Stored.Revision);
        Assert.Equal(ExactTerminalTransitionId, exact.Stored.TransitionId);
        Assert.Equal(Succeeded, exact.Stored.Outcome);
        Assert.Equal(new ScanAccounting(1, 1, 0, 0, 0, 0), accepted.Snapshot.Accounting);
        Assert.Equal(accepted.Snapshot.Lifecycle, retry.Snapshot!.Lifecycle);
        Assert.Equal(accepted.Snapshot.Accounting, retry.Snapshot.Accounting);
        Assert.Equal(accepted.Snapshot.Stages.ToArray(), retry.Snapshot.Stages.ToArray());
    }

    [Fact]
    public async Task SameTransitionDifferentPayloadStaleRevisionAndIllegalEdgeNeverOverwrite()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var (_, initial) = await BootstrapAsync(stores);
        await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(initial, TestContext.Current.CancellationToken);
        await stores.ScanCheckpoints.TransitionStageAsync(new(
            initial.Occurrence.Token.Pin.ScanId, 7, ScanStage.Exact, 1,
            ExactTerminalTransitionId, Succeeded), TestContext.Current.CancellationToken);

        var conflict = await stores.ScanCheckpoints.TransitionStageAsync(new(
            initial.Occurrence.Token.Pin.ScanId, 7, ScanStage.Exact, 1,
            ExactTerminalTransitionId,
            new StageOutcome(ScanStageState.Unsupported, ScanReason.UnsupportedExtension)),
            TestContext.Current.CancellationToken);
        var stale = await stores.ScanCheckpoints.TransitionStageAsync(new(
            initial.Occurrence.Token.Pin.ScanId, 7, ScanStage.Exact, 1,
            Guid.Parse("228f9389-ad3e-405d-b0e8-1820fd60972f"),
            new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled)),
            TestContext.Current.CancellationToken);
        var illegal = await stores.ScanCheckpoints.TransitionStageAsync(new(
            initial.Occurrence.Token.Pin.ScanId, 7, ScanStage.Exact, 2,
            Guid.Parse("9c49e84a-e080-40df-beaf-392a0982f474"),
            new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointWriteOutcome.Conflict, conflict.Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Stale, stale.Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.IllegalTransition, illegal.Outcome);
        var read = await stores.ScanCheckpoints.ReadAsync(
            initial.Occurrence.Token.Pin.ScanId, TestContext.Current.CancellationToken);
        Assert.Equal(Succeeded, Assert.Single(read.Stages,
            stage => stage.Stored.Stage == ScanStage.Exact).Stored.Outcome);
        Assert.Equal(new ScanAccounting(1, 1, 0, 0, 0, 0), read.Accounting);
    }

    [Fact]
    public async Task MissingParentLifecycleOccurrenceAndStageCommandsReturnMissingWithoutRows()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var scan = ScanIdentityData.Scan();
        var initial = Initial(scan);
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(ScanCheckpointWriteOutcome.Missing,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, LifecycleTransitionId, ScanLifecycleState.Preflight), token)).Outcome);
        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Missing,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, LifecycleTransitionId, ScanLifecycleState.Enumerating), token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Missing,
            (await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(initial, token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Missing,
            (await stores.ScanCheckpoints.TransitionStageAsync(new(
                scan.Pin.ScanId, 7, ScanStage.Exact, 1, ExactTerminalTransitionId, Succeeded), token)).Outcome);

        var read = await stores.ScanCheckpoints.ReadAsync(scan.Pin.ScanId, token);
        Assert.Equal(ScanCheckpointReadOutcome.Missing, read.Outcome);
        Assert.Empty((await stores.ScanIdentities.ReadOccurrencesAsync(scan.Pin.ScanId, token)).Records);
    }

    [Fact]
    public async Task FailureBetweenAuthoritativeRowAndAggregateRollsBackBoth()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        var initial = Initial(scan);
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await setup.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken);
            await setup.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, LifecycleTransitionId, ScanLifecycleState.Preflight),
                TestContext.Current.CancellationToken);
            await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(initial, TestContext.Current.CancellationToken);
        }
        var observer = new SqliteDatabaseObserver(
            AfterCheckpointAuthoritativeWrite: (_, _, _) =>
                ValueTask.FromException(new IOException("Injected checkpoint failure.")));

        await using (var failing = new SqlitePhotoSiftStores(fixture.DatabasePath, observer))
        {
            var result = await failing.ScanCheckpoints.TransitionStageAsync(new(
                scan.Pin.ScanId, 7, ScanStage.Exact, 1, ExactTerminalTransitionId, Succeeded),
                TestContext.Current.CancellationToken);
            Assert.Equal(ScanCheckpointWriteOutcome.Unavailable, result.Outcome);
        }

        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var read = await reopened.ScanCheckpoints.ReadAsync(scan.Pin.ScanId, TestContext.Current.CancellationToken);
        Assert.Equal(Running, Assert.Single(read.Stages,
            stage => stage.Stored.Stage == ScanStage.Exact).Stored.Outcome);
        Assert.Equal(1, Assert.Single(read.Stages,
            stage => stage.Stored.Stage == ScanStage.Exact).Stored.Revision);
        Assert.Equal(new ScanAccounting(1, 0, 0, 0, 0, 1), read.Accounting);
    }

    [Fact]
    public async Task NoncanonicalStoredCheckpointPayloadReturnsCorruptInsteadOfNormalizing()
    {
        await using var fixture = new ScanIdentityFixture();
        Guid scanId;
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            var bootstrapped = await BootstrapAsync(stores);
            scanId = bootstrapped.Scan.Pin.ScanId;
            await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
                bootstrapped.Initial, TestContext.Current.CancellationToken);
        }
        await using (var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await ExecuteAsync(raw, """
                UPDATE scan_stage_checkpoints
                SET prerequisite_reason=3
                WHERE stage=1;
                """);
        }

        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var read = await reopened.ScanCheckpoints.ReadAsync(scanId, TestContext.Current.CancellationToken);
        Assert.Equal(ScanCheckpointReadOutcome.Corrupt, read.Outcome);
        Assert.Null(read.Lifecycle);
        Assert.Empty(read.Stages);
        Assert.Null(read.Accounting);
    }

    private static async Task<(ScanIdentityRecord Scan, InitialOccurrenceCheckpointCommand Initial)> BootstrapAsync(
        SqlitePhotoSiftStores stores)
    {
        var scan = ScanIdentityData.Scan();
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, LifecycleTransitionId, ScanLifecycleState.Preflight), token)).Outcome);
        return (scan, Initial(scan));
    }

    private static InitialOccurrenceCheckpointCommand Initial(ScanIdentityRecord scan)
    {
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 7);
        var enumeration = new ScanCheckpoint(
            scan.Pin.ScanId, 7, ScanStage.Enumeration, 1,
            EnumerationTransitionId, Succeeded, null);
        var exact = new ScanCheckpoint(
            scan.Pin.ScanId, 7, ScanStage.Exact, 1,
            ExactStartTransitionId, Running,
            new ScanStagePrerequisite(ScanStage.Enumeration, Succeeded));
        return new(occurrence, enumeration, exact);
    }
}
