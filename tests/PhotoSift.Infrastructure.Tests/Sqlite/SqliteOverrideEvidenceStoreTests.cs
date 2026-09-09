using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Sqlite.Review;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteOverrideEvidenceStoreTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesAtomicallyToVersionOneAndUsesWal()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();

        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);

        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", Assert.IsType<string>(await ScalarAsync(connection, "PRAGMA journal_mode;")),
            ignoreCase: true);
        Assert.Equal(["override_evidence"], await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_' ORDER BY name;"));
        Assert.Equal(OverrideEvidenceData.ExpectedColumns, await ReadStringsAsync(connection,
            "SELECT name FROM pragma_table_info('override_evidence') ORDER BY cid;"));
    }

    [Fact]
    public async Task EveryWriteConnectionEnablesForeignKeysBeforeExecutingCommands()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);

        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, """
                CREATE TRIGGER require_foreign_keys
                BEFORE INSERT ON override_evidence
                WHEN (SELECT foreign_keys FROM pragma_foreign_keys) = 0
                BEGIN
                    SELECT RAISE(ABORT, 'foreign keys disabled');
                END;
                """);
        }

        var second = OverrideEvidenceData.Create(Guid.Parse("d2dcf033-0ab3-4138-8b78-4923ae74472c"));
        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(second, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task ParameterValuesAreBoundAndReadBackWithoutExecutingTheirText()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        var evidence = OverrideEvidenceData.Create() with
        {
            SurvivorPolicyVersion = "survivor-v1'; DROP TABLE override_evidence;--",
            ConsequenceTextVersion = "consequence-'quoted'-v1",
        };

        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);
        var read = await store.ReadAsync(evidence.EvidenceId, TestContext.Current.CancellationToken);

        Assert.Equal(OverrideReadOutcome.Found, read.Outcome);
        OverrideEvidenceData.AssertEquivalent(evidence, read.Evidence);
        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM override_evidence;"));
    }

    [Fact]
    public async Task CanonicalStorageSortsSetsAndUsesLowercaseIdentitiesAndUtc()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        var evidence = OverrideEvidenceData.Create() with
        {
            SelectedOccurrenceIds = [3, 1, 2],
            AffectedPhysicalRevisions = OverrideEvidenceData.Create().AffectedPhysicalRevisions.Reverse().ToImmutableArray(),
        };

        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);

        await using var connection = await fixture.OpenRawAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evidence_id, affirmed_utc, review_session_id, scan_id, group_id,
                   selected_occurrence_ids, affected_physical_revisions, payload_sha256
            FROM override_evidence;
            """;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(evidence.EvidenceId.Value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), reader.GetString(0));
        Assert.EndsWith("Z", reader.GetString(1), StringComparison.Ordinal);
        Assert.Equal(evidence.Pin.Session.ReviewSessionId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), reader.GetString(2));
        Assert.Equal(evidence.Pin.Session.Scan.ScanId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), reader.GetString(3));
        Assert.Equal(evidence.Pin.GroupId.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), reader.GetString(4));
        Assert.Equal([1L, 2L, 3L],
            Assert.IsType<long[]>(JsonSerializer.Deserialize<long[]>(reader.GetString(5))));
        using var affected = JsonDocument.Parse(reader.GetString(6));
        Assert.Equal(2, affected.RootElement.GetArrayLength());
        Assert.Matches("^[0-9a-f]{64}$", reader.GetString(7));
    }

    [Fact]
    public async Task ExactRetryAcceptsCanonicalSetReorderingWithoutAnotherRow()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        var first = OverrideEvidenceData.Create() with { SelectedOccurrenceIds = [3, 1, 2] };
        var retry = first with
        {
            SelectedOccurrenceIds = [2, 3, 1],
            AffectedPhysicalRevisions = first.AffectedPhysicalRevisions.Reverse().ToImmutableArray(),
        };

        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(first, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(OverridePersistOutcome.ExactRetry,
            (await store.PersistAsync(retry, TestContext.Current.CancellationToken)).Outcome);
        var read = await store.ReadAsync(first.EvidenceId, TestContext.Current.CancellationToken);

        Assert.Equal(OverrideReadOutcome.Found, read.Outcome);
        Assert.Equal([1L, 2L, 3L], read.Evidence!.SelectedOccurrenceIds);
        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM override_evidence;"));
    }

    [Fact]
    public async Task ConflictingReuseOfEvidenceIdFailsWithoutReplacingTheFirstPayload()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        var first = OverrideEvidenceData.Create();
        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(first, TestContext.Current.CancellationToken)).Outcome);

        var conflict = first with { SelectedOccurrenceIds = [99] };
        Assert.Equal(OverridePersistOutcome.Conflict,
            (await store.PersistAsync(conflict, TestContext.Current.CancellationToken)).Outcome);
        var read = await store.ReadAsync(first.EvidenceId, TestContext.Current.CancellationToken);

        Assert.Equal(OverrideReadOutcome.Found, read.Outcome);
        OverrideEvidenceData.AssertEquivalent(first, read.Evidence);
    }

    [Fact]
    public async Task ParallelSameEvidenceCallersSerializeToOneInsertAndExactRetries()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        var evidence = OverrideEvidenceData.Create();

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
            store.PersistAsync(evidence, TestContext.Current.CancellationToken).AsTask()));

        Assert.Equal(1, results.Count(result => result.Outcome == OverridePersistOutcome.Persisted));
        Assert.Equal(23, results.Count(result => result.Outcome == OverridePersistOutcome.ExactRetry));
        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM override_evidence;"));
    }

    [Fact]
    public async Task DisposeWaitsForActiveWriterAndLetsItSettleBeforeClosingTheLane()
    {
        await using var fixture = new SqliteOverrideFixture();
        var barrier = new SqliteStoreOperationBarrier();
        var store = fixture.CreateStore(barrier.Observer);
        try
        {
            var write = store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken).AsTask();
            await barrier.FirstLaneEntered;

            var disposal = store.DisposeAsync().AsTask();
            var disposedBeforeRelease = disposal.IsCompleted;
            barrier.ReleaseFirstLane();

            Assert.Equal(OverridePersistOutcome.Persisted, (await write).Outcome);
            await disposal;
            Assert.False(disposedBeforeRelease);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                store.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            barrier.ReleaseFirstLane();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeDrainsActiveAndQueuedWritersBeforeClosingTheLane()
    {
        await using var fixture = new SqliteOverrideFixture();
        var barrier = new SqliteStoreOperationBarrier();
        var store = fixture.CreateStore(barrier.Observer);
        try
        {
            var first = store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken).AsTask();
            await barrier.FirstLaneEntered;
            var secondEvidence = OverrideEvidenceData.Create(Guid.Parse("a8608772-9964-4e58-babd-52aee94cb5bb"));
            var second = store.PersistAsync(secondEvidence, TestContext.Current.CancellationToken).AsTask();
            Assert.False(await barrier.SecondWaitCompletedSynchronously);

            var disposal = store.DisposeAsync().AsTask();
            var disposedBeforeRelease = disposal.IsCompleted;
            barrier.ReleaseFirstLane();

            var firstFailure = await Record.ExceptionAsync(() => first);
            if (disposedBeforeRelease)
            {
                Assert.IsType<ObjectDisposedException>(firstFailure);
                Assert.False(disposedBeforeRelease, "Disposal completed while an active and a queued writer were admitted.");
            }

            Assert.Null(firstFailure);
            var results = await Task.WhenAll(first, second);
            await disposal;
            Assert.All(results, result => Assert.Equal(OverridePersistOutcome.Persisted, result.Outcome));
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                store.PersistAsync(OverrideEvidenceData.Create(Guid.NewGuid()), TestContext.Current.CancellationToken)
                    .AsTask());
        }
        finally
        {
            barrier.ReleaseFirstLane();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task FailureBeforeCommitReturnsUnavailableAndDoesNotInsertOrReplace()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var store = fixture.CreateStore();
        var first = OverrideEvidenceData.Create();
        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(first, TestContext.Current.CancellationToken)).Outcome);
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, """
                CREATE TRIGGER reject_evidence BEFORE INSERT ON override_evidence
                BEGIN SELECT RAISE(ABORT, 'injected pre-commit failure'); END;
                """);
        }

        var rejected = OverrideEvidenceData.Create(Guid.Parse("1e1d6efd-6e2c-4a30-b366-d9cf3a6baee8"));
        Assert.Equal(OverridePersistOutcome.Unavailable,
            (await store.PersistAsync(rejected, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(OverrideReadOutcome.Missing,
            (await store.ReadAsync(rejected.EvidenceId, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(OverrideReadOutcome.Found,
            (await store.ReadAsync(first.EvidenceId, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task LockedOrInvalidDatabasePathReturnsTypedUnavailable()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var initialized = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Persisted,
                (await initialized.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);
        }
        SqliteConnection.ClearAllPools();
        using (var locked = new FileStream(fixture.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        await using (var blocked = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Unavailable,
                (await blocked.PersistAsync(OverrideEvidenceData.Create(Guid.NewGuid()), TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(OverrideReadOutcome.Unavailable,
                (await blocked.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken)).Outcome);
        }

        await using var directoryStore = new SqliteOverrideEvidenceStore(fixture.RootPath);
        Assert.Equal(OverridePersistOutcome.Unavailable,
            (await directoryStore.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task MalformedOrHashMismatchedRowReturnsCorruptWithoutRepair()
    {
        await using var fixture = new SqliteOverrideFixture();
        var evidence = OverrideEvidenceData.Create();
        await using (var store = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Persisted,
                (await store.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);
        }
        await using (var connection = await fixture.OpenRawAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE override_evidence SET selected_occurrence_ids = $malformed WHERE evidence_id = $id;";
            command.Parameters.AddWithValue("$malformed", "not-json");
            command.Parameters.AddWithValue("$id", evidence.EvidenceId.Value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant());
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using var reopened = fixture.CreateStore();
        Assert.Equal(OverrideReadOutcome.Corrupt,
            (await reopened.ReadAsync(evidence.EvidenceId, TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal("not-json", Assert.IsType<string>(await ScalarAsync(verify,
            "SELECT selected_occurrence_ids FROM override_evidence;")));
    }

    [Fact]
    public async Task NullAffectedPayloadElementReturnsTypedCorruptWithoutRepair()
    {
        await using var fixture = new SqliteOverrideFixture();
        var evidence = OverrideEvidenceData.Create();
        await using (var initialized = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Persisted,
                (await initialized.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);
        }
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "UPDATE override_evidence SET affected_physical_revisions = '[null]';");
        }

        await using var store = fixture.CreateStore();
        Assert.Equal(OverrideReadOutcome.Corrupt,
            (await store.ReadAsync(evidence.EvidenceId, TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal("[null]", Assert.IsType<string>(await ScalarAsync(verify,
            "SELECT affected_physical_revisions FROM override_evidence;")));
    }

    [Fact]
    public async Task NullAffectedPayloadElementMakesSameIdRetryAConflictWithoutRepair()
    {
        await using var fixture = new SqliteOverrideFixture();
        var evidence = OverrideEvidenceData.Create();
        await using var store = fixture.CreateStore();
        Assert.Equal(OverridePersistOutcome.Persisted,
            (await store.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "UPDATE override_evidence SET affected_physical_revisions = '[null]';");
        }

        Assert.Equal(OverridePersistOutcome.Conflict,
            (await store.PersistAsync(evidence, TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal("[null]", Assert.IsType<string>(await ScalarAsync(verify,
            "SELECT affected_physical_revisions FROM override_evidence;")));
    }

    [Fact]
    public async Task FailedVersionZeroMigrationLeavesPriorObjectAndVersionUntouched()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "CREATE TABLE override_evidence (legacy TEXT NOT NULL); PRAGMA user_version = 0;");
        }

        await using var store = fixture.CreateStore();
        Assert.Equal(OverridePersistOutcome.Unavailable,
            (await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarLongAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(["legacy"], await ReadStringsAsync(verify,
            "SELECT name FROM pragma_table_info('override_evidence') ORDER BY cid;"));
    }

    [Fact]
    public async Task UnsupportedVersionZeroContentsAreRejectedWithoutChangingAnyDatabaseByte()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection,
                "CREATE TABLE retained (value TEXT NOT NULL); INSERT INTO retained VALUES ('keep'); PRAGMA user_version = 0;");
        }
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);

        await using (var store = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Unavailable,
                (await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken));
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarLongAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(["retained"], await ReadStringsAsync(verify,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_' ORDER BY name;"));
        Assert.Equal("keep", Assert.IsType<string>(await ScalarAsync(verify, "SELECT value FROM retained;")));
    }

    [Fact]
    public async Task SqliteXPrefixedVersionZeroTableIsRejectedWithoutChangingAnyDatabaseByte()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection,
                "CREATE TABLE sqliteXretained (value TEXT NOT NULL); INSERT INTO sqliteXretained VALUES ('keep'); PRAGMA user_version = 0;");
        }
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);

        OverridePersistResult result;
        await using (var store = fixture.CreateStore())
        {
            result = await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken);
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken));
        Assert.Equal(OverridePersistOutcome.Unavailable, result.Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarLongAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(["sqliteXretained"], await ReadStringsAsync(verify,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_' ORDER BY name;"));
        Assert.Equal("keep", Assert.IsType<string>(await ScalarAsync(verify, "SELECT value FROM sqliteXretained;")));
    }

    [Fact]
    public async Task UnsupportedVersionFailureDoesNotChangeTheDatabaseFile()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "CREATE TABLE retained (value TEXT NOT NULL); INSERT INTO retained VALUES ('keep'); PRAGMA user_version = 99;");
        }
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);

        await using (var store = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Unavailable,
                (await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptStartupFailureRetainsEveryOriginalDatabaseByte()
    {
        await using var fixture = new SqliteOverrideFixture();
        byte[] corrupt = [0x50, 0x68, 0x6f, 0x74, 0x6f, 0x53, 0x69, 0x66, 0x74, 0xff, 0x00, 0x7f];
        await File.WriteAllBytesAsync(fixture.DatabasePath, corrupt, TestContext.Current.CancellationToken);

        await using (var store = fixture.CreateStore())
        {
            Assert.Equal(OverridePersistOutcome.Unavailable,
                (await store.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(OverrideReadOutcome.Unavailable,
                (await store.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken)).Outcome);
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(corrupt, await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarAsync(connection, sql), CultureInfo.InvariantCulture);

    private static async Task<string[]> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) values.Add(reader.GetString(0));
        return [.. values];
    }
}

internal sealed class SqliteStoreOperationBarrier
{
    private readonly TaskCompletionSource firstLaneEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource firstLaneRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> secondWaitCompletedSynchronously =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int waitCount;
    private int laneEntryCount;

    internal SqliteStoreOperationBarrier()
    {
        Observer = new(OnLaneWaitStarted, OnLaneEnteredAsync);
    }

    internal SqliteOverrideEvidenceStoreObserver Observer { get; }
    internal Task FirstLaneEntered => firstLaneEntered.Task;
    internal Task<bool> SecondWaitCompletedSynchronously => secondWaitCompletedSynchronously.Task;

    internal void ReleaseFirstLane() => firstLaneRelease.TrySetResult();

    private void OnLaneWaitStarted(bool completedSynchronously)
    {
        if (Interlocked.Increment(ref waitCount) == 2)
        {
            secondWaitCompletedSynchronously.TrySetResult(completedSynchronously);
        }
    }

    private ValueTask OnLaneEnteredAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref laneEntryCount) != 1)
        {
            return ValueTask.CompletedTask;
        }

        firstLaneEntered.TrySetResult();
        return new(firstLaneRelease.Task.WaitAsync(cancellationToken));
    }
}

internal sealed class SqliteOverrideFixture : IAsyncDisposable
{
    internal SqliteOverrideFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "PhotoSift-SqliteOverride-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        DatabasePath = Path.Combine(RootPath, "photosift.db");
    }

    internal string RootPath { get; }
    internal string DatabasePath { get; }
    internal SqliteOverrideEvidenceStore CreateStore() => new(DatabasePath);
    internal SqliteOverrideEvidenceStore CreateStore(SqliteOverrideEvidenceStoreObserver observer) =>
        new(DatabasePath, observer);

    internal async Task<SqliteConnection> OpenRawAsync()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal static class OverrideEvidenceData
{
    internal static readonly OverrideEvidenceId KnownEvidenceId = new(
        Guid.Parse("4382aab0-8088-4c4c-8570-1b5b6aa94961"));

    internal static readonly string[] ExpectedColumns =
    [
        "evidence_id", "affirmed_utc", "review_session_id", "scan_id", "scope_revision",
        "rule_set_revision", "analysis_revision", "group_id", "group_revision",
        "prior_selection_revision", "selected_occurrence_ids", "affected_physical_revisions",
        "survivor_policy_version", "consequence_text_version", "deliberately_affirmed", "payload_sha256",
    ];

    internal static DeliberateOverrideEvidence Create(Guid? evidenceId = null)
    {
        var scan = new ScanPin(Guid.Parse("05ec91d6-7342-4dd4-aedc-2b11795df68e"), 7, 11, 13);
        var key1 = new PhysicalFileKey(19, new(23, 29));
        var key2 = new PhysicalFileKey(31, new(37, 41));
        return new(new(evidenceId ?? KnownEvidenceId.Value),
            new DateTimeOffset(2026, 9, 9, 12, 34, 56, TimeSpan.Zero),
            new(new(Guid.Parse("d264f481-0297-468d-af8c-d8c93a630b62"), scan),
                Guid.Parse("648eaa86-f0ff-4392-8467-fd15a58e0885"), 17, 5),
            [1, 2, 3],
            [
                new(key1, new(key1, 101, new string('A', 64)), new(4096, 1, true)),
                new(key2, new(key2, 202, new string('B', 64)), new(null, null, false)),
            ],
            "survivor-v1", "survivor-consequence-v1", true);
    }

    internal static void AssertEquivalent(DeliberateOverrideEvidence expected, DeliberateOverrideEvidence? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.EvidenceId, actual.EvidenceId);
        Assert.Equal(expected.AffirmedUtc, actual.AffirmedUtc);
        Assert.Equal(expected.Pin, actual.Pin);
        Assert.Equal(expected.SelectedOccurrenceIds.Order(), actual.SelectedOccurrenceIds.Order());
        Assert.Equal(expected.AffectedPhysicalRevisions.OrderBy(Key), actual.AffectedPhysicalRevisions.OrderBy(Key));
        Assert.Equal(expected.SurvivorPolicyVersion, actual.SurvivorPolicyVersion);
        Assert.Equal(expected.ConsequenceTextVersion, actual.ConsequenceTextVersion);
        Assert.Equal(expected.DeliberatelyAffirmed, actual.DeliberatelyAffirmed);
    }

    private static string Key(AffectedPhysicalRevision value) => string.Create(CultureInfo.InvariantCulture,
        $"{value.PhysicalFileKey.VolumeSerial:X16}:{value.PhysicalFileKey.FileId.High:X16}:{value.PhysicalFileKey.FileId.Low:X16}");
}
