using System.Globalization;
using Microsoft.Data.Sqlite;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteSchemaVersionFiveTests
{
    private static readonly string[] ExpectedTables =
    [
        "action_items", "action_revalidations", "action_transitions", "override_evidence",
        "scan_accounting", "scan_embeddings", "scan_file_occurrences", "scan_lifecycle", "scan_stage_checkpoints", "scans",
    ];

    private const string ExpectedLifecycleSql = """
        CREATE TABLE scan_lifecycle (
          scan_id TEXT PRIMARY KEY NOT NULL,
          revision INTEGER NOT NULL CHECK(revision > 0),
          transition_id TEXT NOT NULL CHECK(
            length(transition_id) = 36
            AND transition_id = lower(transition_id)
            AND substr(transition_id, 9, 1) = '-'
            AND substr(transition_id, 14, 1) = '-'
            AND substr(transition_id, 19, 1) = '-'
            AND substr(transition_id, 24, 1) = '-'
            AND length(replace(transition_id, '-', '')) = 32
            AND transition_id != '00000000-0000-0000-0000-000000000000'
            AND transition_id NOT GLOB '*[^0-9a-f-]*'),
          state INTEGER NOT NULL CHECK(state IN (0, 1, 2, 3, 4, 5, 6, 7)),
          FOREIGN KEY(scan_id) REFERENCES scans(scan_id) ON DELETE RESTRICT
        );
        """;

    private const string ExpectedStageSql = """
        CREATE TABLE scan_stage_checkpoints (
          scan_id TEXT NOT NULL,
          occurrence_id INTEGER NOT NULL CHECK(occurrence_id > 0),
          stage INTEGER NOT NULL CHECK(stage IN (0, 1)),
          revision INTEGER NOT NULL CHECK(revision > 0),
          transition_id TEXT NOT NULL CHECK(
            length(transition_id) = 36
            AND transition_id = lower(transition_id)
            AND substr(transition_id, 9, 1) = '-'
            AND substr(transition_id, 14, 1) = '-'
            AND substr(transition_id, 19, 1) = '-'
            AND substr(transition_id, 24, 1) = '-'
            AND length(replace(transition_id, '-', '')) = 32
            AND transition_id != '00000000-0000-0000-0000-000000000000'
            AND transition_id NOT GLOB '*[^0-9a-f-]*'),
          state INTEGER NOT NULL CHECK(state IN (0, 1, 2, 3, 4, 5, 6, 7)),
          reason INTEGER NOT NULL CHECK(reason IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14)),
          prerequisite_stage INTEGER NULL CHECK(prerequisite_stage IS NULL OR prerequisite_stage IN (0, 1)),
          prerequisite_state INTEGER NULL CHECK(prerequisite_state IS NULL OR prerequisite_state IN (0, 1, 2, 3, 4, 5, 6, 7)),
          prerequisite_reason INTEGER NULL CHECK(prerequisite_reason IS NULL OR prerequisite_reason IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14)),
          PRIMARY KEY(scan_id, occurrence_id, stage),
          FOREIGN KEY(scan_id, occurrence_id)
            REFERENCES scan_file_occurrences(scan_id, occurrence_id) ON DELETE RESTRICT,
          CHECK((stage = 0 AND prerequisite_stage IS NULL AND prerequisite_state IS NULL AND prerequisite_reason IS NULL)
             OR (stage = 1 AND prerequisite_stage = 0 AND prerequisite_state IS NOT NULL AND prerequisite_reason IS NOT NULL))
        );
        """;

    private const string ExpectedAccountingSql = """
        CREATE TABLE scan_accounting (
          scan_id TEXT PRIMARY KEY NOT NULL,
          discovered INTEGER NOT NULL CHECK(discovered >= 0),
          analyzed INTEGER NOT NULL CHECK(analyzed >= 0),
          unsupported INTEGER NOT NULL CHECK(unsupported >= 0),
          failed INTEGER NOT NULL CHECK(failed >= 0),
          canceled INTEGER NOT NULL CHECK(canceled >= 0),
          outstanding INTEGER NOT NULL CHECK(outstanding >= 0),
          FOREIGN KEY(scan_id) REFERENCES scan_lifecycle(scan_id) ON DELETE RESTRICT,
          CHECK(discovered = analyzed + unsupported + failed + canceled + outstanding)
        );
        """;

    [Fact]
    public async Task VersionFourFactsSurviveTheExactVersionFiveExtension()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 7);
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistOccurrenceAsync(occurrence, TestContext.Current.CancellationToken)).Outcome);
        }

        string before;
        await using (var versionFour = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await SeedEpicOneFactsAsync(versionFour);
            await ExecuteAsync(versionFour, """
                DROP TABLE scan_embeddings;
                DROP TABLE scan_stage_checkpoints;
                DROP TABLE scan_accounting;
                DROP TABLE scan_lifecycle;
                PRAGMA user_version=4;
                PRAGMA journal_mode=DELETE;
                """);
            await AlbumenSchema.ValidateAsync(versionFour, 4, TestContext.Current.CancellationToken);
            before = await LegacyFactsAsync(versionFour);
        }

        await using (var migrated = new SqliteAlbumenStores(fixture.DatabasePath))
            Assert.Equal(scan, (await migrated.ScanIdentities.ReadScanAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Record);

        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(6L, await ScalarAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(before, await LegacyFactsAsync(verify));
        Assert.Equal(ExpectedTables, await UserTableNamesAsync(verify));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task EveryVersionFiveMigrationStepFailureLeavesVersionFourFactsUnchanged(int faultStep)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken);
            await stores.ScanIdentities.PersistOccurrenceAsync(
                ScanIdentityData.Occurrence(scan.Pin, 7), TestContext.Current.CancellationToken);
        }
        string before;
        await using (var versionFour = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await SeedEpicOneFactsAsync(versionFour);
            await ExecuteAsync(versionFour, """
                DROP TABLE scan_embeddings;
                DROP TABLE scan_stage_checkpoints;
                DROP TABLE scan_accounting;
                DROP TABLE scan_lifecycle;
                PRAGMA user_version=4;
                PRAGMA journal_mode=DELETE;
                """);
            before = await DatabaseSnapshotAsync(versionFour);
        }
        var observer = new SqliteDatabaseObserver(MigrationStep: (step, _, _) =>
            step == faultStep
                ? ValueTask.FromException(new IOException("Injected version-five migration failure."))
                : ValueTask.CompletedTask);

        await using (var rejected = new SqliteAlbumenStores(fixture.DatabasePath, observer))
            Assert.Equal(ScanIdentityReadOutcome.Unavailable,
                (await rejected.ScanIdentities.ReadScanAsync(
                    scan.Pin.ScanId, TestContext.Current.CancellationToken)).Outcome);

        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(before, await DatabaseSnapshotAsync(verify));
    }

    [Fact]
    public async Task FreshDatabaseContainsOnlyTheExactVersionFiveSchema()
    {
        await using var fixture = new ScanIdentityFixture();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
            _ = await stores.ScanIdentities.ReadScanAsync(
                Guid.Parse("d318e25e-0c09-42df-918c-d63337ad1b9f"), TestContext.Current.CancellationToken);

        await using var connection = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(6L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(ExpectedTables, await UserTableNamesAsync(connection));
        Assert.Equal(Normalize(ExpectedLifecycleSql), await TableSqlAsync(connection, "scan_lifecycle"));
        Assert.Equal(Normalize(ExpectedStageSql), await TableSqlAsync(connection, "scan_stage_checkpoints"));
        Assert.Equal(Normalize(ExpectedAccountingSql), await TableSqlAsync(connection, "scan_accounting"));
    }

    [Theory]
    [InlineData("scan_lifecycle")]
    [InlineData("scan_stage_checkpoints")]
    public async Task CheckpointTablesRejectTheCanonicalEmptyTransitionId(string table)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            var token = TestContext.Current.CancellationToken;
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                    scan.Pin.ScanId,
                    1,
                    Guid.Parse("df95504d-46b4-4a01-b593-cc64801484c0"),
                    ScanLifecycleState.Preflight), token)).Outcome);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                    scan.Pin.ScanId,
                    1,
                    Guid.Parse("6b378b5d-e531-4689-9e41-a2a5128c55dc"),
                    ScanLifecycleState.Enumerating), token)).Outcome);
            var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(new(
                    ScanIdentityData.Occurrence(scan.Pin, 7),
                    new(scan.Pin.ScanId, 7, ScanStage.Enumeration, 1,
                        Guid.Parse("be3f6bd3-d7a1-49e3-8d3d-b83b01e26010"), succeeded, null),
                    new(scan.Pin.ScanId, 7, ScanStage.Exact, 1,
                        Guid.Parse("4f46773c-894e-43d2-a66a-2b946d9109c7"),
                        new(ScanStageState.Running, ScanReason.None),
                        new(ScanStage.Enumeration, succeeded))), token)).Outcome);
        }

        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(raw,
            $"UPDATE {table} SET transition_id='00000000-0000-0000-0000-000000000000';"));
    }

    [Fact]
    public async Task VersionFiveRejectsAnExtraObjectWithoutWritingTheDatabase()
    {
        await using var fixture = new ScanIdentityFixture();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
            _ = await stores.ScanIdentities.ReadScanAsync(
                Guid.Parse("d318e25e-0c09-42df-918c-d63337ad1b9f"), TestContext.Current.CancellationToken);
        await using (var mutate = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
            await ExecuteAsync(mutate, "CREATE TABLE foreign_checkpoint(value TEXT NOT NULL);");
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);

        await using (var rejected = new SqliteAlbumenStores(fixture.DatabasePath))
            Assert.Equal(ScanIdentityReadOutcome.Unavailable,
                (await rejected.ScanIdentities.ReadScanAsync(
                    ScanIdentityData.Scan().Pin.ScanId, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken));
    }

    private static async Task<string[]> UserTableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) names.Add(reader.GetString(0));
        return [.. names];
    }

    private static async Task<string> TableSqlAsync(SqliteConnection connection, string table) => Normalize(
        Assert.IsType<string>(await ScalarAsync(connection,
            $"SELECT sql FROM sqlite_schema WHERE type='table' AND name='{table}';")));

    private static async Task<string> LegacyFactsAsync(SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (var table in new[]
                 {
                     "action_items", "action_revalidations", "action_transitions", "override_evidence",
                     "scan_file_occurrences", "scans",
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

    internal static async Task SeedEpicOneFactsAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            INSERT INTO override_evidence VALUES (
              'evidence-1', '2026-09-14T10:00:00.0000000Z', 'review-1', 'scan-legacy',
              2, 3, 4, 'group-1', 5, 6, '[1]', '[]', 'policy-v1', 'text-v1', 1, 'override-hash');
            INSERT INTO action_items VALUES (
              'action-1', 'request-1', 'request-hash', 7, 8,
              '2026-09-14T10:01:00.0000000Z', 'requested-payload', 'requested-hash');
            INSERT INTO action_transitions VALUES (
              'action-1', 2, 'executing', '2026-09-14T10:02:00.0000000Z',
              'transition-payload', 'transition-hash');
            INSERT INTO action_revalidations VALUES (
              'revalidation-1', 'action-1', 1, '2026-09-14T10:03:00.0000000Z',
              'confirmed_present', 'original_file_confirmed', 'observation-payload', 'observation-hash');
            """);
    }

    internal static async Task<string> DatabaseSnapshotAsync(SqliteConnection connection)
    {
        var tables = await UserTableNamesAsync(connection);
        var snapshot = new List<string>
        {
            Convert.ToString(await ScalarAsync(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture) ?? "<null>",
            Convert.ToString(await ScalarAsync(connection,
                "SELECT group_concat(type || ':' || name || ':' || tbl_name || ':' || ifnull(sql, '<null>'), char(10)) FROM (SELECT * FROM sqlite_schema ORDER BY name);"),
                CultureInfo.InvariantCulture) ?? "<null>",
        };
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                snapshot.Add(table + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? "<null>" :
                        Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }
        return string.Join('\n', snapshot);
    }

    private static string Normalize(string sql) => string.Join(' ', sql.Trim().TrimEnd(';')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
