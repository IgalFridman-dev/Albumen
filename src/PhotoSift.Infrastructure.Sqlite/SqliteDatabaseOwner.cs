using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PhotoSift.Infrastructure.Sqlite.Review;

namespace PhotoSift.Infrastructure.Sqlite;

internal enum SqliteWriteDurability
{
    Normal,
    ActionFull,
}

internal sealed class SqliteDatabaseOwner : IAsyncDisposable
{
    private readonly string databasePath;
    private readonly object lifecycle = new();
    private readonly SqliteDatabaseObserver? observer;
    private readonly Channel<IWriteCommand> writes = Channel.CreateUnbounded<IWriteCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly TaskCompletionSource initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? pump;
    private Task? disposal;
    private TaskCompletionSource? drained;
    private int admitted;
    private int pendingWrites;

    internal SqliteDatabaseOwner(string databasePath, SqliteDatabaseObserver? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.observer = observer;
        _ = initialized.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    internal Task<T> RunReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);
        token.ThrowIfCancellationRequested();
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            admitted++;
            EnsurePumpStarted();
        }

        // Microsoft.Data.Sqlite's async surface performs synchronous native work.
        // A reader therefore owns one worker and one read-only connection.
        return Task.Run(async () =>
        {
            try
            {
                await initialized.Task.WaitAsync(token).ConfigureAwait(false);
                await using var connection = await OpenReaderAsync(token).ConfigureAwait(false);
                return await work(connection, token).ConfigureAwait(false);
            }
            finally { CompleteAdmission(); }
        }, CancellationToken.None);
    }

    internal Task<T> RunWriteAsync<T>(SqliteWriteDurability durability,
        Func<SqliteConnection, CancellationToken, Task<T>> work, CancellationToken token,
        SqliteOverrideEvidenceStoreObserver? overrideObserver = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Enum.IsDefined(durability)) throw new ArgumentOutOfRangeException(nameof(durability));
        token.ThrowIfCancellationRequested();
        var command = new WriteCommand<T>(durability, work, overrideObserver, this, token);
        bool laneWasFree;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            admitted++;
            laneWasFree = pendingWrites++ == 0 && initialized.Task.IsCompletedSuccessfully;
            EnsurePumpStarted();
            if (!writes.Writer.TryWrite(command))
            {
                pendingWrites--;
                admitted--;
                throw new ObjectDisposedException(nameof(SqliteDatabaseOwner));
            }
        }
        try
        {
            observer?.LaneWaitStarted?.Invoke(laneWasFree);
            overrideObserver?.LaneWaitStarted(laneWasFree);
            command.AcceptAdmission();
            return command.Completion;
        }
        catch (Exception exception)
        {
            command.RejectAdmission(exception);
            throw;
        }
    }

    private void EnsurePumpStarted() => pump ??= Task.Run(PumpAsync);

    private async Task PumpAsync()
    {
        SqliteConnection? connection = null;
        Exception? initializationFailure = null;
        try
        {
            try
            {
                connection = await OpenWriterAsync(CancellationToken.None).ConfigureAwait(false);
                initialized.TrySetResult();
            }
            catch (Exception exception)
            {
                initializationFailure = exception;
                initialized.TrySetException(exception);
            }

            await foreach (var command in writes.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (initializationFailure is not null)
                    command.FailInitialization(initializationFailure);
                else
                    await command.ExecuteAsync(connection!).ConfigureAwait(false);
            }
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenWriterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        RefuseRetainedSidecars();
        var existing = File.Exists(databasePath);
        if (existing)
        {
            // A sidecar-free immutable admission validates source bytes without
            // checkpointing or otherwise recovering journal state.
            await using var admissionGuard = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            RefuseRetainedSidecars();
            await using var admission = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = new Uri(databasePath).AbsoluteUri + "?immutable=1",
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            await admission.OpenAsync(token).ConfigureAwait(false);
            await PhotoSiftSchema.ValidateSourceAsync(admission, token).ConfigureAwait(false);
        }
        RefuseRetainedSidecars();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = existing ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await PhotoSiftSchema.InitializeAsync(connection, observer, token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, setWal: true, token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenReaderAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, setWal: false, token).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, bool setWal,
        CancellationToken token)
    {
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", token).ConfigureAwait(false);
        if (await ScalarAsync(connection, "PRAGMA foreign_keys;", token).ConfigureAwait(false) is not long keys || keys != 1)
            throw new InvalidDataException("Foreign keys could not be enabled.");
        var journalSql = setWal ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode;";
        if (await ScalarAsync(connection, journalSql, token).ConfigureAwait(false) is not string mode
            || !mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WAL could not be verified.");
    }

    private static async Task ConfigureDurabilityAsync(SqliteConnection connection,
        SqliteWriteDurability durability, CancellationToken token)
    {
        var (name, expected) = durability switch
        {
            SqliteWriteDurability.Normal => ("NORMAL", 1L),
            SqliteWriteDurability.ActionFull => ("FULL", 2L),
            _ => throw new ArgumentOutOfRangeException(nameof(durability)),
        };
        await ExecuteAsync(connection, $"PRAGMA synchronous={name};", token).ConfigureAwait(false);
        if (await ScalarAsync(connection, "PRAGMA synchronous;", token).ConfigureAwait(false) is not long actual
            || actual != expected)
            throw new InvalidDataException($"{name} durability could not be verified.");
    }

    private void RefuseRetainedSidecars()
    {
        if (Path.Exists(databasePath + "-wal") || Path.Exists(databasePath + "-shm")
            || Path.Exists(databasePath + "-journal"))
            throw new InvalidDataException("Retained SQLite journal state requires separate recovery authority.");
    }

    internal ValueTask AfterActionInsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token) => observer?.AfterActionInsert?.Invoke(connection, transaction, token)
            ?? ValueTask.CompletedTask;

    private void CompleteWriteAdmission()
    {
        lock (lifecycle)
        {
            pendingWrites--;
            CompleteAdmissionCore();
        }
    }

    private void CompleteAdmission()
    {
        lock (lifecycle) CompleteAdmissionCore();
    }

    private void CompleteAdmissionCore()
    {
        if (--admitted == 0) drained?.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is null)
            {
                writes.Writer.TryComplete();
                var drain = admitted == 0 ? Task.CompletedTask
                    : (drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                disposal = DisposeAfterDrainAsync(drain, pump ?? Task.CompletedTask);
            }
            return new(disposal);
        }
    }

    private static async Task DisposeAfterDrainAsync(Task drain, Task pump)
    {
        await drain.ConfigureAwait(false);
        await pump.ConfigureAwait(false);
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    internal static bool IsUnavailable(Exception exception) => exception is SqliteException or IOException
        or UnauthorizedAccessException or InvalidDataException;

    private interface IWriteCommand
    {
        ValueTask ExecuteAsync(SqliteConnection connection);
        void FailInitialization(Exception exception);
    }

    private sealed class WriteCommand<T>(SqliteWriteDurability durability,
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        SqliteOverrideEvidenceStoreObserver? overrideObserver, SqliteDatabaseOwner owner,
        CancellationToken token) : IWriteCommand
    {
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> admission =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<T> Completion => completion.Task;
        internal void AcceptAdmission() => admission.TrySetResult(true);
        internal void RejectAdmission(Exception exception)
        {
            completion.TrySetException(exception);
            _ = completion.Task.Exception;
            admission.TrySetResult(false);
        }

        public async ValueTask ExecuteAsync(SqliteConnection connection)
        {
            try
            {
                if (!await admission.Task.ConfigureAwait(false)) return;
                token.ThrowIfCancellationRequested();
                if (owner.observer?.LaneEntered is { } entered)
                    await entered(token).ConfigureAwait(false);
                if (overrideObserver is not null)
                    await overrideObserver.LaneEntered(token).ConfigureAwait(false);
                await ConfigureDurabilityAsync(connection, durability, token).ConfigureAwait(false);
                if (overrideObserver?.ConnectionInitialized is { } initialized)
                    await initialized(connection, token).ConfigureAwait(false);
                if (durability == SqliteWriteDurability.ActionFull
                    && owner.observer?.BeforeActionTransaction is { } before)
                    await before(connection, token).ConfigureAwait(false);
                completion.TrySetResult(await work(connection, token).ConfigureAwait(false));
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { owner.CompleteWriteAdmission(); }
        }

        public void FailInitialization(Exception exception)
        {
            completion.TrySetException(exception);
            owner.CompleteWriteAdmission();
        }
    }
}

internal sealed record SqliteDatabaseObserver(
    System.Action<bool>? LaneWaitStarted = null,
    Func<CancellationToken, ValueTask>? LaneEntered = null,
    Func<int, SqliteConnection, CancellationToken, ValueTask>? MigrationStep = null,
    Func<SqliteConnection, CancellationToken, ValueTask>? BeforeActionTransaction = null,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>? AfterActionInsert = null);
