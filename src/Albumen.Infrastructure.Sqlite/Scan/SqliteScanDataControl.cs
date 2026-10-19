using Microsoft.Data.Sqlite;
using Albumen.Application.Action;
using Albumen.Application.LocalData;
using Albumen.Infrastructure.Sqlite.Action;
using System.Text.Json;

namespace Albumen.Infrastructure.Sqlite.Scan;

internal sealed class SqliteScanDataControl(SqliteDatabaseOwner owner) : ILocalScanDataControl
{
    private const string ClearedMessage =
        "Local Scan data was cleared. Source photos and Action records were unchanged.";
    private const string BlockedMessage =
        "Local Scan data was retained because an Action is unfinished or unknown. Reconcile the Action record, then try again.";
    private const string FailureMessage =
        "Local Scan data could not be cleared and was retained. Check the local database, then try again.";

    public async ValueTask<ScanDataClearResult> ClearAsync(CancellationToken cancellationToken)
    {
        long? retainedScanCount = null;
        try
        {
            return await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await AlbumenSchema.ValidateAsync(
                    connection, AlbumenSchema.Version, token, transaction).ConfigureAwait(false);
                retainedScanCount = await CountAsync(
                    connection, "scans", transaction, token).ConfigureAwait(false);
                var actions = await SqliteActionRecordStore.LoadInTransactionAsync(
                    connection, transaction, token).ConfigureAwait(false);
                if (actions.Any(IsBlockingAction))
                {
                    return new ScanDataClearResult(
                        ScanDataClearOutcome.BlockedByActionReconciliation,
                        0,
                        retainedScanCount,
                        BlockedMessage);
                }

                await DeleteAsync(connection, "scan_accounting", transaction, token).ConfigureAwait(false);
                await DeleteAsync(connection, "scan_stage_checkpoints", transaction, token).ConfigureAwait(false);
                await DeleteAsync(connection, "scan_lifecycle", transaction, token).ConfigureAwait(false);
                await DeleteAsync(connection, "scan_file_occurrences", transaction, token).ConfigureAwait(false);
                await owner.AfterScanDataChildrenDeletedAsync(connection, transaction, token).ConfigureAwait(false);
                await DeleteAsync(connection, "scans", transaction, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new ScanDataClearResult(
                    ScanDataClearOutcome.Cleared,
                    retainedScanCount.Value,
                    0,
                    ClearedMessage);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception)
            || exception is JsonException or ArgumentException or InvalidOperationException
                or OverflowException or FormatException)
        {
            return new ScanDataClearResult(
                ScanDataClearOutcome.RetainedDatabaseFailure,
                0,
                retainedScanCount,
                FailureMessage);
        }
    }

    private static bool IsBlockingAction(PersistedActionItem item) => item.Terminal is null
        || item.Terminal.Kind == ActionTerminalKind.UnknownPendingRevalidation
        && !item.Revalidations.Any(value => value.Kind == ActionRevalidationKind.ConfirmedPresent);

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("SQLite count was unavailable."));
    }

    private static async Task DeleteAsync(
        SqliteConnection connection,
        string table,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table};";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
