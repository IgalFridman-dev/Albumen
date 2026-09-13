using Microsoft.Data.Sqlite;
using PhotoSift.Infrastructure.Sqlite.Review;

namespace PhotoSift.Infrastructure.Sqlite;

internal sealed class SqliteDatabaseOwner : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly object lifecycle = new();
    private readonly SqliteDatabaseObserver? observer;
    private Task? disposal;
    private TaskCompletionSource? drained;
    private int admitted;

    internal SqliteDatabaseOwner(string databasePath, SqliteDatabaseObserver? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.observer = observer;
    }

    internal Task<T> RunAsync<T>(bool actionWrite, Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken token, SqliteOverrideEvidenceStoreObserver? overrideObserver = null)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            admitted++;
        }
        // The provider's async methods perform synchronous native work. Always
        // enter through a worker, even for a free lane and completed awaits.
        return Task.Run(async () =>
        {
            var entered = false;
            try
            {
                var wait = lane.WaitAsync(token);
                observer?.LaneWaitStarted?.Invoke(wait.IsCompleted);
                overrideObserver?.LaneWaitStarted(wait.IsCompleted);
                await wait.ConfigureAwait(false);
                entered = true;
                if (observer?.LaneEntered is { } onEntry) await onEntry(token).ConfigureAwait(false);
                if (overrideObserver is not null) await overrideObserver.LaneEntered(token).ConfigureAwait(false);
                await using var connection = await OpenAsync(actionWrite, token).ConfigureAwait(false);
                if (overrideObserver?.ConnectionInitialized is { } initialized) await initialized(connection, token).ConfigureAwait(false);
                return await work(connection, token).ConfigureAwait(false);
            }
            finally
            {
                if (entered) lane.Release();
                lock (lifecycle)
                {
                    if (--admitted == 0) drained?.TrySetResult();
                }
            }
        }, CancellationToken.None);
    }

    private async Task<SqliteConnection> OpenAsync(bool actionWrite, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        RefuseRetainedSidecars();
        var existing = File.Exists(databasePath);
        if (existing)
        {
            // Ordinary read-only SQLite opens can create WAL/SHM files. First
            // exclude every sidecar and hold the main file against writers;
            // only this proven sidecar-free snapshot may use immutable mode.
            // It is never used to inspect or ignore retained WAL/journal data.
            await using var admissionGuard = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RefuseRetainedSidecars();
            await using var admission = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = new Uri(databasePath).AbsoluteUri + "?immutable=1",
                Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1,
            }.ToString());
            await admission.OpenAsync(token).ConfigureAwait(false);
            await PhotoSiftSchema.ValidateSourceAsync(admission, token).ConfigureAwait(false);
        }
        RefuseRetainedSidecars();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = existing ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 1,
        }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await PhotoSiftSchema.InitializeAsync(connection, observer, token).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", token).ConfigureAwait(false);
            if (await ScalarAsync(connection, "PRAGMA foreign_keys;", token).ConfigureAwait(false) is not long keys || keys != 1)
                throw new InvalidDataException("Foreign keys could not be enabled.");
            if (await ScalarAsync(connection, "PRAGMA journal_mode=WAL;", token).ConfigureAwait(false) is not string mode || mode != "wal")
                throw new InvalidDataException("WAL could not be enabled.");
            if (actionWrite)
            {
                await ExecuteAsync(connection, "PRAGMA synchronous=FULL;", token).ConfigureAwait(false);
                if (await ScalarAsync(connection, "PRAGMA synchronous;", token).ConfigureAwait(false) is not long full || full != 2)
                    throw new InvalidDataException("FULL durability could not be verified.");
                if (observer?.BeforeActionTransaction is { } before) await before(connection, token).ConfigureAwait(false);
            }
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void RefuseRetainedSidecars()
    {
        // Recovery/checkpointing is outside this journal's authority, including
        // cleanup of empty or stale sidecars. Refuse before any SQLite open.
        if (Path.Exists(databasePath + "-wal") || Path.Exists(databasePath + "-shm") || Path.Exists(databasePath + "-journal"))
            throw new InvalidDataException("Retained SQLite journal state requires separate recovery authority.");
    }

    internal ValueTask AfterActionInsertAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token) =>
        observer?.AfterActionInsert?.Invoke(connection, transaction, token) ?? ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is null)
            {
                var wait = admitted == 0 ? Task.CompletedTask
                    : (drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                disposal = DisposeAfterDrainAsync(wait);
            }
            return new(disposal);
        }
    }
    private async Task DisposeAfterDrainAsync(Task wait)
    {
        await wait.ConfigureAwait(false);
        lane.Dispose();
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }
    internal static bool IsUnavailable(Exception exception) => exception is SqliteException or IOException
        or UnauthorizedAccessException or InvalidDataException;
}

internal sealed record SqliteDatabaseObserver(
    System.Action<bool>? LaneWaitStarted = null,
    Func<CancellationToken, ValueTask>? LaneEntered = null,
    Func<int, SqliteConnection, CancellationToken, ValueTask>? MigrationStep = null,
    Func<SqliteConnection, CancellationToken, ValueTask>? BeforeActionTransaction = null,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>? AfterActionInsert = null);
