using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Review;
using PhotoSift.Infrastructure.Sqlite;
using PhotoSift.Infrastructure.Sqlite.Review;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteSchemaVersionTwoTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EmptyAndCanonicalVersionOneMigrateThroughTheExactVersionTwoExtension(int version)
    {
        await using var fixture = new SqliteOverrideFixture();
        await SeedAsync(fixture, version);
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        Assert.Equal(OverrideReadOutcome.Missing,
            (await stores.OverrideEvidence.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken)).Outcome);
        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal(4L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal("action_items,action_revalidations,action_transitions,override_evidence,scan_file_occurrences,scans,sqlite_autoindex_action_items_1,sqlite_autoindex_action_items_2,sqlite_autoindex_action_revalidations_1,sqlite_autoindex_action_revalidations_2,sqlite_autoindex_action_transitions_1,sqlite_autoindex_override_evidence_1,sqlite_autoindex_scan_file_occurrences_1,sqlite_autoindex_scans_1",
            await ScalarAsync(connection, "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_schema ORDER BY name);"));
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task VersionOneEvidenceSurvivesMigrationWithEveryStoredColumnUnchanged()
    {
        await using var fixture = new SqliteOverrideFixture();
        // Accepted override encoding supplies a real retained row; strip only
        // the extension to construct the exact accepted predecessor database.
        var evidence = OverrideEvidenceData.Create();
        await using (var store = fixture.CreateStore())
            Assert.Equal(OverridePersistOutcome.Persisted,
                (await store.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);
        string before;
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS scan_file_occurrences; DROP TABLE IF EXISTS scans; DROP TABLE IF EXISTS action_revalidations; DROP TABLE IF EXISTS action_transitions; DROP TABLE IF EXISTS action_items; PRAGMA user_version=1; PRAGMA journal_mode=DELETE;");
            before = await OverrideRowAsync(connection);
        }
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            var read = await stores.OverrideEvidence.ReadAsync(evidence.EvidenceId, TestContext.Current.CancellationToken);
            Assert.Equal(OverrideReadOutcome.Found, read.Outcome);
            OverrideEvidenceData.AssertEquivalent(evidence, read.Evidence);
        }
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(before, await OverrideRowAsync(verify));
        Assert.Equal(4L, await ScalarAsync(verify, "PRAGMA user_version;"));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)] [InlineData(0, 4)] [InlineData(0, 5)] [InlineData(0, 6)]
    [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)] [InlineData(1, 4)] [InlineData(1, 5)]
    public async Task EveryMigrationCreateVersionAndTargetValidationFaultRollsBackBytesAndJournalMode(int version, int faultStep)
    {
        await using var fixture = new SqliteOverrideFixture();
        await SeedAsync(fixture, version);
        var observer = new SqliteDatabaseObserver(MigrationStep: (step, _, _) =>
        {
            if (step == faultStep) throw new IOException("Migration fault before commit");
            return ValueTask.CompletedTask;
        });
        await AssertRejectedUnchangedAsync(fixture, observer);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal((long)version, await ScalarAsync(verify, "PRAGMA user_version;"));
    }

    [Theory]
    [InlineData(0, "CREATE TABLE retained(value TEXT);")]
    [InlineData(1, "CREATE TABLE retained(value TEXT);")]
    [InlineData(1, "CREATE INDEX foreign_index ON override_evidence(scan_id);")]
    [InlineData(1, "CREATE VIEW foreign_view AS SELECT * FROM override_evidence;")]
    [InlineData(1, "CREATE TRIGGER foreign_trigger AFTER INSERT ON override_evidence BEGIN SELECT 1; END;")]
    [InlineData(1, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'group_revision > 0','group_revision >= 0') WHERE name='override_evidence'; PRAGMA writable_schema=OFF;")]
    [InlineData(2, "CREATE INDEX foreign_index ON action_items(request_id);")]
    [InlineData(2, "CREATE TABLE retained(value TEXT);")]
    [InlineData(2, "CREATE VIEW foreign_view AS SELECT * FROM action_items;")]
    [InlineData(2, "CREATE TRIGGER foreign_trigger BEFORE INSERT ON action_items BEGIN SELECT RAISE(IGNORE); END;")]
    [InlineData(2, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'sequence IN (2, 3)','sequence IN (1, 2, 3)') WHERE name='action_transitions'; PRAGMA writable_schema=OFF;")]
    [InlineData(2, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'REFERENCES action_items(action_item_id)','REFERENCES override_evidence(evidence_id)') WHERE name='action_transitions'; PRAGMA writable_schema=OFF;")]
    [InlineData(2, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'requested_utc TEXT NOT NULL','requested_utc INTEGER') WHERE name='action_items'; PRAGMA writable_schema=OFF;")]
    [InlineData(2, "PRAGMA user_version=99;")]
    public async Task ClosedSchemaRejectsExtraOrRewrittenObjectsBeforeAnyWalWrite(int version, string mutation)
    {
        await using var fixture = new SqliteOverrideFixture();
        await SeedAsync(fixture, version);
        await using (var connection = await fixture.OpenRawAsync()) await ExecuteAsync(connection, mutation);
        await AssertRejectedUnchangedAsync(fixture);
    }

    internal static async Task SeedAsync(SqliteOverrideFixture fixture, int version)
    {
        if (version == 2)
        {
            await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
            Assert.Equal(OverrideReadOutcome.Missing,
                (await stores.OverrideEvidence.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken)).Outcome);
            await stores.DisposeAsync();
            await using var exactV2 = await fixture.OpenRawAsync();
            await ExecuteAsync(exactV2, "DROP TABLE scan_file_occurrences; DROP TABLE scans; DROP TABLE action_revalidations; PRAGMA user_version=2; PRAGMA journal_mode=DELETE;");
            return;
        }
        await using var connection = await fixture.OpenRawAsync();
        if (version == 1) await ExecuteAsync(connection, OverrideEvidenceSchema.CreateTableSql);
        await ExecuteAsync(connection, $"PRAGMA user_version={version}; PRAGMA journal_mode=DELETE;");
    }

    private static async Task AssertRejectedUnchangedAsync(SqliteOverrideFixture fixture, SqliteDatabaseObserver? observer = null)
    {
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);
        object? mode;
        await using (var connection = await fixture.OpenRawAsync()) mode = await ScalarAsync(connection, "PRAGMA journal_mode;");
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath, observer))
            Assert.Equal(OverrideReadOutcome.Unavailable,
                (await stores.OverrideEvidence.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken));
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(mode, await ScalarAsync(verify, "PRAGMA journal_mode;"));
    }

    private static async Task<string> OverrideRowAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM override_evidence;";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)));
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
