using Microsoft.Data.Sqlite;
using Albumen.Infrastructure.Sqlite.Action;
using Albumen.Infrastructure.Sqlite.Review;
using Albumen.Infrastructure.Sqlite.Scan;

namespace Albumen.Infrastructure.Sqlite;

internal static class AlbumenSchema
{
    internal const int Version = 5;

    internal static async Task ValidateAsync(SqliteConnection connection, int expectedVersion,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        if (await SqliteDatabaseOwner.ScalarAsync(connection, "PRAGMA user_version;", token, transaction).ConfigureAwait(false)
            is not long version || version != expectedVersion || expectedVersion is < 0 or > Version)
            throw new InvalidDataException("Unsupported SQLite version.");
        var expected = new Dictionary<string, (string Type, string Table, string? Sql)>(StringComparer.Ordinal);
        if (expectedVersion > 0)
        {
            expected.Add("override_evidence", ("table", "override_evidence", OverrideEvidenceSchema.CreateTableSql));
            expected.Add("sqlite_autoindex_override_evidence_1", ("index", "override_evidence", null));
        }
        if (expectedVersion >= 2)
        {
            expected.Add("action_items", ("table", "action_items", ActionJournalSchema.CreateItemsSql));
            expected.Add("action_transitions", ("table", "action_transitions", ActionJournalSchema.CreateTransitionsSql));
            expected.Add("sqlite_autoindex_action_items_1", ("index", "action_items", null));
            expected.Add("sqlite_autoindex_action_items_2", ("index", "action_items", null));
            expected.Add("sqlite_autoindex_action_transitions_1", ("index", "action_transitions", null));
        }
        if (expectedVersion >= 3)
        {
            expected.Add("action_revalidations", ("table", "action_revalidations", ActionRevalidationSchema.CreateTableSql));
            expected.Add("sqlite_autoindex_action_revalidations_1", ("index", "action_revalidations", null));
            expected.Add("sqlite_autoindex_action_revalidations_2", ("index", "action_revalidations", null));
        }
        if (expectedVersion >= 4)
        {
            expected.Add("scans", ("table", "scans", ScanIdentitySchema.CreateScansSql));
            expected.Add("scan_file_occurrences", ("table", "scan_file_occurrences", ScanIdentitySchema.CreateOccurrencesSql));
            expected.Add("sqlite_autoindex_scans_1", ("index", "scans", null));
            expected.Add("sqlite_autoindex_scan_file_occurrences_1", ("index", "scan_file_occurrences", null));
        }
        if (expectedVersion >= 5)
        {
            expected.Add("scan_lifecycle", ("table", "scan_lifecycle", ScanCheckpointSchema.CreateLifecycleSql));
            expected.Add("scan_stage_checkpoints", ("table", "scan_stage_checkpoints", ScanCheckpointSchema.CreateStagesSql));
            expected.Add("scan_accounting", ("table", "scan_accounting", ScanCheckpointSchema.CreateAccountingSql));
            expected.Add("sqlite_autoindex_scan_lifecycle_1", ("index", "scan_lifecycle", null));
            expected.Add("sqlite_autoindex_scan_stage_checkpoints_1", ("index", "scan_stage_checkpoints", null));
            expected.Add("sqlite_autoindex_scan_accounting_1", ("index", "scan_accounting", null));
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (!expected.Remove(reader.GetString(1), out var definition)
                || reader.GetString(0) != definition.Type || reader.GetString(2) != definition.Table
                || (definition.Sql is null ? !reader.IsDBNull(3)
                    : reader.IsDBNull(3) || Normalize(reader.GetString(3)) != Normalize(definition.Sql)))
                throw new InvalidDataException("Unexpected SQLite schema object or definition.");
        }
        if (expected.Count != 0) throw new InvalidDataException("Missing SQLite schema object.");
    }

    internal static async Task<int> ValidateSourceAsync(SqliteConnection connection, CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != "ok"
                || await reader.ReadAsync(token).ConfigureAwait(false))
                throw new InvalidDataException("SQLite integrity validation failed.");
        }
        var version = await SqliteDatabaseOwner.ScalarAsync(connection, "PRAGMA user_version;", token).ConfigureAwait(false);
        if (version is not long source || source is < 0 or > Version)
            throw new InvalidDataException("Unsupported SQLite version.");
        await ValidateAsync(connection, (int)source, token).ConfigureAwait(false);
        return (int)source;
    }

    internal static async Task InitializeAsync(SqliteConnection connection, SqliteDatabaseObserver? observer, CancellationToken token)
    {
        var source = await ValidateSourceAsync(connection, token).ConfigureAwait(false);
        if (source == Version) return;
        await SqliteDatabaseOwner.ExecuteAsync(connection, "BEGIN EXCLUSIVE;", token).ConfigureAwait(false);
        try
        {
            await ValidateAsync(connection, source, token).ConfigureAwait(false);
            var step = 0;
            if (source == 0)
            {
                await SqliteDatabaseOwner.ExecuteAsync(connection, OverrideEvidenceSchema.CreateTableSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
            }
            if (source < 2)
            {
                await SqliteDatabaseOwner.ExecuteAsync(connection, ActionJournalSchema.CreateItemsSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
                await SqliteDatabaseOwner.ExecuteAsync(connection, ActionJournalSchema.CreateTransitionsSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
            }
            if (source < 3)
            {
                await SqliteDatabaseOwner.ExecuteAsync(connection, ActionRevalidationSchema.CreateTableSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
            }
            if (source < 4)
            {
                await SqliteDatabaseOwner.ExecuteAsync(connection, ScanIdentitySchema.CreateScansSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
                await SqliteDatabaseOwner.ExecuteAsync(connection, ScanIdentitySchema.CreateOccurrencesSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
            }
            if (source < 5)
            {
                await SqliteDatabaseOwner.ExecuteAsync(connection, ScanCheckpointSchema.CreateLifecycleSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
                await SqliteDatabaseOwner.ExecuteAsync(connection, ScanCheckpointSchema.CreateStagesSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
                await SqliteDatabaseOwner.ExecuteAsync(connection, ScanCheckpointSchema.CreateAccountingSql, token).ConfigureAwait(false);
                await ObserveAsync(++step).ConfigureAwait(false);
            }
            await SqliteDatabaseOwner.ExecuteAsync(connection, "PRAGMA user_version=5;", token).ConfigureAwait(false);
            await ObserveAsync(++step).ConfigureAwait(false);
            await ValidateAsync(connection, Version, token).ConfigureAwait(false);
            await ObserveAsync(++step).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await SqliteDatabaseOwner.ExecuteAsync(connection, "COMMIT;", token).ConfigureAwait(false);
        }
        catch
        {
            try { await SqliteDatabaseOwner.ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false); }
            catch (SqliteException) { /* Keep the original failure. */ }
            throw;
        }
        ValueTask ObserveAsync(int step) => observer?.MigrationStep?.Invoke(step, connection, token) ?? ValueTask.CompletedTask;
    }

    private static string Normalize(string sql) => string.Join(' ', sql.Trim().TrimEnd(';')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
