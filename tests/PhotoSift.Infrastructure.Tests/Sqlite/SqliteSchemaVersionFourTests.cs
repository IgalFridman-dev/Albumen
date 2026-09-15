using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteSchemaVersionFourTests
{
    private static readonly string[] ExpectedTables =
    [
        "action_items", "action_revalidations", "action_transitions",
        "override_evidence", "scan_accounting", "scan_file_occurrences",
        "scan_lifecycle", "scan_stage_checkpoints", "scans",
    ];

    private const string ExpectedScansSql = """
        CREATE TABLE scans (
          scan_id TEXT PRIMARY KEY NOT NULL,
          scope_revision INTEGER NOT NULL CHECK(scope_revision > 0),
          rule_set_revision INTEGER NOT NULL CHECK(rule_set_revision > 0),
          analysis_revision INTEGER NOT NULL CHECK(analysis_revision > 0),
          selected_path TEXT NOT NULL CHECK(length(selected_path) > 0),
          resolved_path TEXT NOT NULL CHECK(length(resolved_path) > 0),
          recursion INTEGER NOT NULL CHECK(recursion IN (0, 1)),
          root_volume_serial TEXT NOT NULL CHECK(length(root_volume_serial) = 16),
          root_file_id_high TEXT NOT NULL CHECK(length(root_file_id_high) = 16),
          root_file_id_low TEXT NOT NULL CHECK(length(root_file_id_low) = 16),
          created_utc TEXT NOT NULL
        );
        """;

    private const string ExpectedOccurrencesSql = """
        CREATE TABLE scan_file_occurrences (
          scan_id TEXT NOT NULL,
          occurrence_id INTEGER NOT NULL CHECK(occurrence_id > 0),
          occurrence_revision INTEGER NOT NULL CHECK(occurrence_revision > 0),
          attempt INTEGER NOT NULL CHECK(attempt > 0),
          discovered_path TEXT NOT NULL CHECK(length(discovered_path) > 0),
          resolved_path TEXT NOT NULL CHECK(length(resolved_path) > 0),
          volume_serial TEXT NULL CHECK(volume_serial IS NULL OR length(volume_serial) = 16),
          file_id_high TEXT NULL CHECK(file_id_high IS NULL OR length(file_id_high) = 16),
          file_id_low TEXT NULL CHECK(file_id_low IS NULL OR length(file_id_low) = 16),
          PRIMARY KEY(scan_id, occurrence_id),
          FOREIGN KEY(scan_id) REFERENCES scans(scan_id) ON DELETE RESTRICT,
          CHECK((volume_serial IS NULL AND file_id_high IS NULL AND file_id_low IS NULL)
             OR (volume_serial IS NOT NULL AND file_id_high IS NOT NULL AND file_id_low IS NOT NULL))
        );
        """;

    [Fact]
    public async Task VersionZeroEmptyRetainsTheExactVersionFourTablesWithinCurrentSchema()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var connection = await fixture.OpenRawAsync();

        await PhotoSiftSchema.InitializeAsync(connection, observer: null, TestContext.Current.CancellationToken);

        Assert.Equal(5L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(ExpectedTables, await UserTableNamesAsync(connection));
        Assert.Equal(Normalize(ExpectedScansSql), await TableSqlAsync(connection, "scans"));
        Assert.Equal(Normalize(ExpectedOccurrencesSql), await TableSqlAsync(connection, "scan_file_occurrences"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryPredecessorFactSurvivesMigrationWithEveryStoredColumnUnchanged(int version)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var connection = await fixture.OpenRawAsync();
        await SeedExactPredecessorAsync(connection, version);

        await PhotoSiftSchema.InitializeAsync(connection, observer: null, TestContext.Current.CancellationToken);

        Assert.Equal(5L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(OverrideRow, await RowAsync(connection, "override_evidence"));
        if (version >= 2)
        {
            Assert.Equal(ActionItemRow, await RowAsync(connection, "action_items"));
            Assert.Equal(ActionTransitionRow, await RowAsync(connection, "action_transitions"));
        }
        if (version >= 3) Assert.Equal(ActionRevalidationRow, await RowAsync(connection, "action_revalidations"));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)] [InlineData(0, 4)]
    [InlineData(0, 5)] [InlineData(0, 6)] [InlineData(0, 7)] [InlineData(0, 8)]
    [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)] [InlineData(1, 4)]
    [InlineData(1, 5)] [InlineData(1, 6)] [InlineData(1, 7)]
    [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)] [InlineData(2, 4)] [InlineData(2, 5)]
    [InlineData(3, 1)] [InlineData(3, 2)] [InlineData(3, 3)] [InlineData(3, 4)]
    public async Task EveryDdlVersionAndBeforeCommitFaultLeavesPriorSchemaVersionAndFactsUnchanged(
        int version, int faultStep)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var connection = await fixture.OpenRawAsync();
        await SeedExactPredecessorAsync(connection, version);
        var before = await SnapshotAsync(connection);
        var observer = new SqliteDatabaseObserver(MigrationStep: (step, _, _) =>
            step == faultStep ? ValueTask.FromException(new IOException("Injected migration failure.")) : ValueTask.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() =>
            PhotoSiftSchema.InitializeAsync(connection, observer, TestContext.Current.CancellationToken));

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Theory]
    [InlineData(0, "CREATE TABLE retained(value TEXT NOT NULL); INSERT INTO retained VALUES ('keep');")]
    [InlineData(3, "CREATE TABLE retained(value TEXT NOT NULL); INSERT INTO retained VALUES ('keep');")]
    [InlineData(3, "CREATE VIEW foreign_view AS SELECT * FROM override_evidence;")]
    [InlineData(3, "CREATE INDEX foreign_index ON action_items(request_id);")]
    [InlineData(99, "CREATE TABLE retained(value TEXT NOT NULL); INSERT INTO retained VALUES ('keep');")]
    public async Task UnsupportedVersionOrForeignObjectIsRejectedWithoutChangingAnything(int version, string mutation)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var connection = await fixture.OpenRawAsync();
        if (version == 3) await SeedExactPredecessorAsync(connection, version);
        await ExecuteAsync(connection, mutation);
        await ExecuteAsync(connection, $"PRAGMA user_version={version};");
        var before = await SnapshotAsync(connection);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PhotoSiftSchema.InitializeAsync(connection, observer: null, TestContext.Current.CancellationToken));

        Assert.Equal(before, await SnapshotAsync(connection));
    }

    private const string OverrideRow =
        "evidence-1|2026-09-14T10:00:00.0000000Z|review-1|scan-legacy|2|3|4|group-1|5|6|[1]|[]|policy-v1|text-v1|1|override-hash";
    private const string ActionItemRow =
        "action-1|request-1|request-hash|7|8|2026-09-14T10:01:00.0000000Z|requested-payload|requested-hash";
    private const string ActionTransitionRow =
        "action-1|2|executing|2026-09-14T10:02:00.0000000Z|transition-payload|transition-hash";
    private const string ActionRevalidationRow =
        "revalidation-1|action-1|1|2026-09-14T10:03:00.0000000Z|confirmed_present|original_file_confirmed|observation-payload|observation-hash";

    private static async Task SeedExactPredecessorAsync(SqliteConnection connection, int version)
    {
        if (version >= 1)
        {
            await ExecuteAsync(connection, PhotoSift.Infrastructure.Sqlite.Review.OverrideEvidenceSchema.CreateTableSql);
            await ExecuteAsync(connection, """
                INSERT INTO override_evidence VALUES (
                  'evidence-1', '2026-09-14T10:00:00.0000000Z', 'review-1', 'scan-legacy',
                  2, 3, 4, 'group-1', 5, 6, '[1]', '[]', 'policy-v1', 'text-v1', 1, 'override-hash');
                """);
        }
        if (version >= 2)
        {
            await ExecuteAsync(connection, PhotoSift.Infrastructure.Sqlite.Action.ActionJournalSchema.CreateItemsSql);
            await ExecuteAsync(connection, PhotoSift.Infrastructure.Sqlite.Action.ActionJournalSchema.CreateTransitionsSql);
            await ExecuteAsync(connection, """
                INSERT INTO action_items VALUES (
                  'action-1', 'request-1', 'request-hash', 7, 8,
                  '2026-09-14T10:01:00.0000000Z', 'requested-payload', 'requested-hash');
                INSERT INTO action_transitions VALUES (
                  'action-1', 2, 'executing', '2026-09-14T10:02:00.0000000Z',
                  'transition-payload', 'transition-hash');
                """);
        }
        if (version >= 3)
        {
            await ExecuteAsync(connection, PhotoSift.Infrastructure.Sqlite.Action.ActionRevalidationSchema.CreateTableSql);
            await ExecuteAsync(connection, """
                INSERT INTO action_revalidations VALUES (
                  'revalidation-1', 'action-1', 1, '2026-09-14T10:03:00.0000000Z',
                  'confirmed_present', 'original_file_confirmed', 'observation-payload', 'observation-hash');
                """);
        }
        await ExecuteAsync(connection, $"PRAGMA user_version={version};");
        await PhotoSiftSchema.ValidateAsync(connection, version, TestContext.Current.CancellationToken);
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

    private static async Task<string> RowAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table};";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return string.Join('|', Enumerable.Range(0, reader.FieldCount)
            .Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)));
    }

    private static async Task<string> SnapshotAsync(SqliteConnection connection)
    {
        var tables = await UserTableNamesAsync(connection);
        var rows = new List<string>();
        foreach (var table in tables) rows.Add($"{table}:{await RowsAsync(connection, table)}");
        var snapshot = new List<string>
        {
            Convert.ToString(await ScalarAsync(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture) ?? "<null>",
            Convert.ToString(await ScalarAsync(connection,
                "SELECT group_concat(type || ':' || name || ':' || tbl_name || ':' || ifnull(sql, '<null>'), char(10)) FROM (SELECT * FROM sqlite_schema ORDER BY name);"),
                CultureInfo.InvariantCulture) ?? "<null>",
        };
        snapshot.AddRange(rows);
        return string.Join("\n", snapshot);
    }

    private static async Task<string> RowsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table};";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        return string.Join("\n", rows);
    }

    private static string Normalize(string sql) => string.Join(' ', sql.Trim().TrimEnd(';')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
