using System.Globalization;
using Microsoft.Data.Sqlite;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionFiveTests;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

/// <summary>Every 1.0 database is at version five: its facts must survive the photo vector table.</summary>
public sealed class SqliteSchemaVersionSixTests
{
    private static readonly StageOutcome Succeeded = new(ScanStageState.Succeeded, ScanReason.None);

    [Fact]
    public async Task VersionFiveScanFactsSurviveTheVersionSixExtension()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await SeedVersionFiveScanAsync(fixture, scan);
        string before;
        await using (var versionFive = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await DowngradeToVersionFiveAsync(versionFive);
            await AlbumenSchema.ValidateAsync(versionFive, 5, TestContext.Current.CancellationToken);
            before = await RowsAsync(versionFive);
        }

        ScanCheckpointReadResult checkpoints;
        await using (var migrated = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(scan, (await migrated.ScanIdentities.ReadScanAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Record);
            checkpoints = await migrated.ScanCheckpoints.ReadAsync(scan.Pin.ScanId, TestContext.Current.CancellationToken);
        }

        Assert.Equal(ScanCheckpointReadOutcome.Found, checkpoints.Outcome);
        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(6L, await ScalarAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(before, await RowsAsync(verify));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT count(*) FROM scan_embeddings;"));
    }

    // Source five runs three steps: the vector table, the version, the validation.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryVersionSixMigrationStepFailureLeavesVersionFiveUnchanged(int faultStep)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await SeedVersionFiveScanAsync(fixture, scan);
        string before;
        await using (var versionFive = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await DowngradeToVersionFiveAsync(versionFive);
            before = await DatabaseSnapshotAsync(versionFive);
        }
        var observer = new SqliteDatabaseObserver(MigrationStep: (step, _, _) =>
            step == faultStep
                ? ValueTask.FromException(new IOException("Injected version-six migration failure."))
                : ValueTask.CompletedTask);

        await using (var rejected = new SqliteAlbumenStores(fixture.DatabasePath, observer))
            Assert.Equal(ScanIdentityReadOutcome.Unavailable,
                (await rejected.ScanIdentities.ReadScanAsync(
                    scan.Pin.ScanId, TestContext.Current.CancellationToken)).Outcome);

        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(before, await DatabaseSnapshotAsync(verify));
    }

    private static async Task SeedVersionFiveScanAsync(ScanIdentityFixture fixture, ScanIdentityRecord scan)
    {
        var token = TestContext.Current.CancellationToken;
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                    scan.Pin.ScanId, 1, Transition(1), ScanLifecycleState.Preflight), token)).Outcome);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                    scan.Pin.ScanId, 1, Transition(2), ScanLifecycleState.Enumerating), token)).Outcome);
            var occurrence = ScanIdentityData.Occurrence(scan.Pin, 7);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(new(
                    occurrence,
                    new ScanCheckpoint(scan.Pin.ScanId, 7, ScanStage.Enumeration, 1, Transition(107), Succeeded, null),
                    new ScanCheckpoint(scan.Pin.ScanId, 7, ScanStage.Exact, 1, Transition(107), Succeeded,
                        new ScanStagePrerequisite(ScanStage.Enumeration, Succeeded))), token)).Outcome);
        }
        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite);
        await SeedEpicOneFactsAsync(raw);
    }

    private static Task DowngradeToVersionFiveAsync(SqliteConnection connection) => ExecuteAsync(connection, """
        DROP TABLE scan_embeddings;
        PRAGMA user_version=5;
        PRAGMA journal_mode=DELETE;
        """);

    // Every stored row of the version-five tables.
    private static async Task<string> RowsAsync(SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (var table in new[]
                 {
                     "action_items", "action_revalidations", "action_transitions", "override_evidence",
                     "scan_accounting", "scan_file_occurrences", "scan_lifecycle", "scan_stage_checkpoints", "scans",
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(table + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? "<null>" :
                        Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }
        return string.Join('\n', rows);
    }

    private static Guid Transition(long suffix) =>
        Guid.Parse($"00000000-0000-4000-8000-{suffix:000000000000}");
}
