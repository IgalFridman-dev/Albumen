using Microsoft.Data.Sqlite;
using PhotoSift.Application.Action;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteSchemaVersionThreeTests
{
    [Fact]
    public async Task FreshDatabaseRetainsTheExactVersionThreeSchemaWithinCurrentSchema()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
            Assert.Equal(ActionRecordLoadOutcome.Loaded,
                (await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);

        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal(4L, await ScalarAsync(connection, "PRAGMA user_version;"));
        Assert.Equal("action_items,action_revalidations,action_transitions,override_evidence,scan_file_occurrences,scans,sqlite_autoindex_action_items_1,sqlite_autoindex_action_items_2,sqlite_autoindex_action_revalidations_1,sqlite_autoindex_action_revalidations_2,sqlite_autoindex_action_transitions_1,sqlite_autoindex_override_evidence_1,sqlite_autoindex_scan_file_occurrences_1,sqlite_autoindex_scans_1",
            await ScalarAsync(connection, "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_schema ORDER BY name);"));
        Assert.Equal("revalidation_id,action_item_id,sequence,observed_utc,result_kind,reason,observation_payload,observation_payload_sha256",
            await ScalarAsync(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('action_revalidations');"));
    }

    [Fact]
    public async Task VersionTwoMigratesWithoutChangingExistingActionRows()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            Assert.Equal(ActionJournalWriteOutcome.Persisted,
                (await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ActionJournalWriteOutcome.Persisted,
                (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(requested), TestContext.Current.CancellationToken)).Outcome);
        }
        string itemBefore;
        string transitionBefore;
        await using (var connection = await fixture.OpenRawAsync())
        {
            itemBefore = await RowAsync(connection, "action_items");
            transitionBefore = await RowAsync(connection, "action_transitions");
            await ExecuteAsync(connection, "DROP TABLE scan_file_occurrences; DROP TABLE scans; DROP TABLE action_revalidations; PRAGMA user_version=2; PRAGMA journal_mode=DELETE;");
        }

        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
            Assert.Equal(ActionRecordLoadOutcome.Loaded,
                (await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);

        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(4L, await ScalarAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(itemBefore, await RowAsync(verify, "action_items"));
        Assert.Equal(transitionBefore, await RowAsync(verify, "action_transitions"));
    }

    [Theory]
    [InlineData("CREATE INDEX foreign_index ON action_revalidations(action_item_id);")]
    [InlineData("CREATE VIEW foreign_view AS SELECT * FROM action_revalidations;")]
    [InlineData("PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'sequence INTEGER NOT NULL','sequence REAL NOT NULL') WHERE name='action_revalidations'; PRAGMA writable_schema=OFF;")]
    [InlineData("PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'''confirmed_present'', ''still_unknown''','''confirmed_present'', ''still_unknown'', ''moved''') WHERE name='action_revalidations'; PRAGMA writable_schema=OFF;")]
    public async Task ClosedVersionThreeSchemaRejectsExtraOrRewrittenObjects(string mutation)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
            Assert.Equal(ActionRecordLoadOutcome.Loaded,
                (await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode=DELETE;");
            await ExecuteAsync(connection, mutation);
        }

        await using var rejected = new SqlitePhotoSiftStores(fixture.DatabasePath);
        Assert.Equal(ActionRecordLoadOutcome.Unavailable,
            (await rejected.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryVersionTwoMigrationStepFaultRollsBackTableAndVersion(int faultStep)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var initialized = new SqlitePhotoSiftStores(fixture.DatabasePath))
            _ = await initialized.ActionRecords.LoadAsync(TestContext.Current.CancellationToken);
        await using (var connection = await fixture.OpenRawAsync())
            await ExecuteAsync(connection, "DROP TABLE scan_file_occurrences; DROP TABLE scans; DROP TABLE action_revalidations; PRAGMA user_version=2; PRAGMA journal_mode=DELETE;");
        var observer = new SqliteDatabaseObserver(MigrationStep: (step, _, _) =>
            step == faultStep ? ValueTask.FromException(new IOException("migration fault")) : ValueTask.CompletedTask);

        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath, observer))
            Assert.Equal(ActionRecordLoadOutcome.Unavailable,
                (await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);

        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(2L, await ScalarAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name='action_revalidations';"));
        Assert.Equal("delete", await ScalarAsync(verify, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task LoadRejectsAnOrphanTransitionInsteadOfOmittingIt()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
            await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(requested), TestContext.Current.CancellationToken);
        }
        await using (var connection = await fixture.OpenRawAsync())
            await ExecuteAsync(connection,
                "PRAGMA foreign_keys=OFF; UPDATE action_transitions SET action_item_id='99999999-9999-9999-9999-999999999999';");

        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);
        Assert.Equal(ActionRecordLoadOutcome.Unavailable,
            (await reopened.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    private static async Task<string> RowAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table};";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue));
    }
}
