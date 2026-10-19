using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Sqlite.Review;
using Xunit;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteReadWriteOwnershipTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SynchronousAdmissionObserverFailureSkipsWriteAndBalancesDisposal(bool databaseObserver)
    {
        await using var fixture = new SqliteOverrideFixture();
        var expected = new InvalidOperationException("injected admission observer failure");
        Action<bool> fail = _ => throw expected;
        var ownerObserver = databaseObserver ? new SqliteDatabaseObserver(LaneWaitStarted: fail) : null;
        var overrideObserver = databaseObserver ? null
            : new SqliteOverrideEvidenceStoreObserver(fail, _ => ValueTask.CompletedTask);
        var owner = new SqliteDatabaseOwner(fixture.DatabasePath, ownerObserver);
        var callbackEntries = 0;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                Interlocked.Increment(ref callbackEntries);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await SqliteDatabaseOwner.ExecuteAsync(connection,
                    "CREATE TABLE observer_probe(value INTEGER NOT NULL);", token, transaction);
                await transaction.CommitAsync(token);
                return 0;
            }, TestContext.Current.CancellationToken, overrideObserver));

        Assert.Same(expected, actual);
        await owner.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, callbackEntries);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0, Convert.ToInt64(await SqliteDatabaseOwner.ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='observer_probe';",
            TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ThreeAdmittedWritesEnterInExactFifoOrderWhileFirstIsBlocked()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var order = new ConcurrentQueue<int>();

        var first = owner.RunWriteAsync(SqliteWriteDurability.Normal, async (_, token) =>
        {
            order.Enqueue(1);
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) =>
        {
            order.Enqueue(2);
            return Task.FromResult(2);
        }, TestContext.Current.CancellationToken);
        var third = owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) =>
        {
            order.Enqueue(3);
            return Task.FromResult(3);
        }, TestContext.Current.CancellationToken);

        Assert.Equal([1], order);
        releaseFirst.TrySetResult();
        var results = await Task.WhenAll(first, second, third);
        Assert.Equal([1, 2, 3], results);
        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task ShortLivedReadersUseDistinctConnectionsAndCanOverlap()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        await InitializeAsync(owner);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Signal();
        var connections = new ConcurrentQueue<SqliteConnection>();
        var entries = 0;

        Task<int> ReadAsync() => owner.RunReadAsync(async (connection, token) =>
        {
            connections.Enqueue(connection);
            if (Interlocked.Increment(ref entries) == 2) bothEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);

        var first = ReadAsync();
        var second = ReadAsync();
        await bothEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, connections.Count);
        Assert.NotSame(connections.ElementAt(0), connections.ElementAt(1));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal([1, 1], results);
    }

    [Fact]
    public async Task ReaderCompletesAgainstCommittedSnapshotWhileWriteTransactionIsOpen()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            await SqliteDatabaseOwner.ExecuteAsync(connection,
                "CREATE TABLE ownership_probe(value INTEGER NOT NULL); INSERT INTO ownership_probe VALUES (1);",
                token, transaction);
            await transaction.CommitAsync(token);
            return 0;
        }, TestContext.Current.CancellationToken);
        var inserted = Signal();
        var releaseWrite = Signal();
        var write = owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            await SqliteDatabaseOwner.ExecuteAsync(connection, "INSERT INTO ownership_probe VALUES (2);", token, transaction);
            inserted.TrySetResult();
            await releaseWrite.Task.WaitAsync(token);
            await transaction.CommitAsync(token);
            return 0;
        }, TestContext.Current.CancellationToken);
        await inserted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var count = await owner.RunReadAsync(async (connection, token) =>
            Convert.ToInt64(await SqliteDatabaseOwner.ScalarAsync(connection,
                "SELECT COUNT(*) FROM ownership_probe;", token), CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        releaseWrite.TrySetResult();
        await write;
    }

    [Fact]
    public async Task NestedWriteAdmissionDoesNotReenterTheActiveCallback()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        Task<int>? nested = null;
        var nestedEntered = false;
        var outer = owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) =>
        {
            nested = owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) =>
            {
                nestedEntered = true;
                return Task.FromResult(2);
            }, TestContext.Current.CancellationToken);
            Assert.False(nestedEntered);
            return Task.FromResult(1);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, await outer);
        Assert.NotNull(nested);
        Assert.Equal(2, await nested);
        Assert.True(nestedEntered);
    }

    [Fact]
    public async Task ReaderConnectionsAreReadOnlyAndVerifyRequiredPragmas()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);

        var values = await owner.RunReadAsync(async (connection, token) =>
        {
            var keys = Convert.ToInt64(await SqliteDatabaseOwner.ScalarAsync(connection, "PRAGMA foreign_keys;", token),
                CultureInfo.InvariantCulture);
            var mode = Assert.IsType<string>(await SqliteDatabaseOwner.ScalarAsync(connection, "PRAGMA journal_mode;", token));
            var failure = await Assert.ThrowsAsync<SqliteException>(() =>
                SqliteDatabaseOwner.ExecuteAsync(connection, "CREATE TABLE forbidden(value INTEGER);", token));
            return (keys, mode, failure.SqliteErrorCode);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, values.keys);
        Assert.Equal("wal", values.mode, ignoreCase: true);
        Assert.Equal(8, values.SqliteErrorCode);
    }

    [Fact]
    public async Task WriterSetsNormalAndActionFullDurabilityBeforeCallbacks()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);

        var normal = await ReadSynchronousAsync(SqliteWriteDurability.Normal);
        var full = await ReadSynchronousAsync(SqliteWriteDurability.ActionFull);
        var normalAgain = await ReadSynchronousAsync(SqliteWriteDurability.Normal);

        Assert.Equal(1, normal);
        Assert.Equal(2, full);
        Assert.Equal(1, normalAgain);

        Task<long> ReadSynchronousAsync(SqliteWriteDurability durability) =>
            owner.RunWriteAsync(durability, async (connection, token) =>
                Convert.ToInt64(await SqliteDatabaseOwner.ScalarAsync(connection, "PRAGMA synchronous;", token),
                    CultureInfo.InvariantCulture),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancellationAndCallbackFailureDoNotStopLaterFifoWrites()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var first = owner.RunWriteAsync(SqliteWriteDurability.Normal, async (_, token) =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        var canceledCallbackEntries = 0;
        var second = owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) =>
        {
            Interlocked.Increment(ref canceledCallbackEntries);
            return Task.FromResult(2);
        }, canceled.Token);
        var third = owner.RunWriteAsync<int>(SqliteWriteDurability.Normal,
            (_, _) => Task.FromException<int>(new IOException("injected callback failure")),
            TestContext.Current.CancellationToken);
        var fourth = owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) => Task.FromResult(4),
            TestContext.Current.CancellationToken);
        canceled.Cancel();
        releaseFirst.TrySetResult();

        Assert.Equal(1, await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(0, canceledCallbackEntries);
        await Assert.ThrowsAsync<IOException>(() => third);
        Assert.Equal(4, await fourth);
    }

    [Fact]
    public async Task DisposalRejectsNewAdmissionAndDrainsAdmittedReadAndWriteExactlyOnce()
    {
        await using var fixture = new SqliteOverrideFixture();
        var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        await InitializeAsync(owner);
        var writeEntered = Signal();
        var readEntered = Signal();
        var release = Signal();
        var writeEntries = 0;
        var readEntries = 0;
        var write = owner.RunWriteAsync(SqliteWriteDurability.Normal, async (_, token) =>
        {
            Interlocked.Increment(ref writeEntries);
            writeEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);
        var read = owner.RunReadAsync(async (_, token) =>
        {
            Interlocked.Increment(ref readEntries);
            readEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 2;
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(writeEntered.Task, readEntered.Task).WaitAsync(TestContext.Current.CancellationToken);

        var disposal = owner.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.RunReadAsync((_, _) => Task.FromResult(0),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.RunWriteAsync(SqliteWriteDurability.Normal,
            (_, _) => Task.FromResult(0), TestContext.Current.CancellationToken));
        release.TrySetResult();
        var results = await Task.WhenAll(write, read);
        Assert.Equal([1, 2], results);
        await disposal;
        await owner.DisposeAsync();
        Assert.Equal(1, writeEntries);
        Assert.Equal(1, readEntries);
    }

    private static Task<int> InitializeAsync(SqliteDatabaseOwner owner) =>
        owner.RunWriteAsync(SqliteWriteDurability.Normal, (_, _) => Task.FromResult(0),
            TestContext.Current.CancellationToken);

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
