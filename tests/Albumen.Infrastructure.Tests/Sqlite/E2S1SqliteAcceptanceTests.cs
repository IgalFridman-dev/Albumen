using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Albumen.App;
using Albumen.App.Composition;
using Albumen.App.Shell;
using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class E2S1SqliteAcceptanceTests
{
    private const string BlockedMessage =
        "Local data could not be safely loaded. Albumen will not modify files until the database is available.";
    private static readonly int[] ExpectedWriteOrder = [1, 2];

    [Fact]
    public async Task PopulatedVersionThreeMigratesWithoutChangingEpicOneRowsAndSupportsScanIdentitySnapshotsAndSerializedWrites()
    {
        await using var fixture = new SqliteOverrideFixture();
        await SeedPopulatedVersionThreeAsync(fixture);
        string[] epicOneBefore;
        await using (var before = await fixture.OpenRawAsync())
            epicOneBefore = await EpicOneRowsAsync(before);

        var scan = ScanIdentityData.Scan();
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 7);
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistOccurrenceAsync(occurrence, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(scan, (await stores.ScanIdentities.ReadScanAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Record);
            Assert.Equal([occurrence], (await stores.ScanIdentities.ReadOccurrencesAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Records);
        }

        await using (var migrated = await fixture.OpenRawAsync())
        {
            Assert.Equal(6L, await ScalarAsync(migrated, "PRAGMA user_version;"));
            Assert.Equal(epicOneBefore, await EpicOneRowsAsync(migrated));
        }

        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);
        var firstInserted = Signal();
        var releaseFirst = Signal();
        var order = new List<int>();
        var first = owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            order.Add(1);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await SqliteDatabaseOwner.ExecuteAsync(connection, ScanInsertSql("00000000-0000-0000-0000-000000000002"), token, transaction);
            firstInserted.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
            await transaction.CommitAsync(token);
            return 1;
        }, TestContext.Current.CancellationToken);
        await firstInserted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            order.Add(2);
            await SqliteDatabaseOwner.ExecuteAsync(connection, ScanInsertSql("00000000-0000-0000-0000-000000000003"), token);
            return 2;
        }, TestContext.Current.CancellationToken);

        var committedSnapshot = await owner.RunReadAsync(async (connection, token) =>
            Convert.ToInt64(await SqliteDatabaseOwner.ScalarAsync(
                connection, "SELECT COUNT(*) FROM scans;", token), CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);
        Assert.Equal(1L, committedSnapshot);
        Assert.Equal([1], order);
        releaseFirst.TrySetResult();
        Assert.Equal(ExpectedWriteOrder, await Task.WhenAll(first, second));
        Assert.Equal(ExpectedWriteOrder, order);
        Assert.Equal(3L, await owner.RunReadAsync(async (connection, token) =>
            Convert.ToInt64(await SqliteDatabaseOwner.ScalarAsync(
                connection, "SELECT COUNT(*) FROM scans;", token), CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken));
        await using var final = await fixture.OpenRawAsync();
        Assert.Equal(epicOneBefore, await EpicOneRowsAsync(final));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("foreign")]
    [InlineData("read-only")]
    [InlineData("retained-sidecar")]
    public async Task UnsafeStartupFixturesRemainUnavailableAndUnchanged(string kind)
    {
        await using var fixture = new SqliteOverrideFixture();
        await PrepareUnsafeFixtureAsync(fixture, kind);
        var before = await SnapshotFilesAsync(fixture.DatabasePath);
        try
        {
            await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
            Assert.Equal(ActionRecordLoadOutcome.Unavailable,
                (await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
        }
        finally
        {
            if (kind == "read-only") File.SetAttributes(fixture.DatabasePath, FileAttributes.Normal);
        }
        Assert.Equal(before, await SnapshotFilesAsync(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("foreign")]
    [InlineData("read-only")]
    [InlineData("retained-sidecar")]
    public async Task ProductionStartupProjectsEveryUnsafeDatabaseBeforeLeavingChooseScopeAndNeverInvokesRecycleBin(
        string kind)
    {
        await using var fixture = new SqliteOverrideFixture();
        await PrepareUnsafeFixtureAsync(fixture, kind);
        var executor = new CountingRecycleBinExecutor();
        try
        {
            RunOnSta(() =>
            {
                var viewModel = CompositionRoot.CreateShellViewModel(fixture.DatabasePath, executor);
                var window = new MainWindow { DataContext = viewModel };
                try
                {
                    window.Show();
                    WaitFor(() => viewModel.ActionRecoveryStatusText == BlockedMessage);

                    Assert.True(viewModel.IsChooseScopePhase);
                    Assert.Equal(BlockedMessage, viewModel.ActionRecoveryStatusText);
                    var status = Assert.IsType<TextBlock>(window.FindName("LocalDataStatusText"));
                    Assert.True(status.IsVisible);
                    Assert.Equal(BlockedMessage, status.Text);
                    Assert.Equal(BlockedMessage, AutomationProperties.GetItemStatus(status));
                    Assert.Equal(0, executor.Executions);
                }
                finally
                {
                    window.Close();
                    var disposal = viewModel.DisposeAsync().AsTask();
                    WaitFor(() => disposal.IsCompleted);
                    disposal.GetAwaiter().GetResult();
                }
            });
        }
        finally
        {
            if (kind == "read-only") File.SetAttributes(fixture.DatabasePath, FileAttributes.Normal);
        }
        Assert.Equal(0, executor.Executions);
    }

    private static async Task PrepareUnsafeFixtureAsync(SqliteOverrideFixture fixture, string kind)
    {
        if (kind is "read-only" or "retained-sidecar")
            await SeedPopulatedVersionThreeAsync(fixture);
        else if (kind == "corrupt")
            await File.WriteAllBytesAsync(fixture.DatabasePath, [0x50, 0x68, 0x6f, 0x74, 0xff], TestContext.Current.CancellationToken);
        else
        {
            await using var raw = await fixture.OpenRawAsync();
            await ExecuteAsync(raw, "CREATE TABLE retained(value TEXT NOT NULL); INSERT INTO retained VALUES ('keep');");
        }

        if (kind == "retained-sidecar")
            await File.WriteAllBytesAsync(fixture.DatabasePath + "-wal", [], TestContext.Current.CancellationToken);
        if (kind == "read-only")
            File.SetAttributes(fixture.DatabasePath, File.GetAttributes(fixture.DatabasePath) | FileAttributes.ReadOnly);
    }

    private static async Task SeedPopulatedVersionThreeAsync(SqliteOverrideFixture fixture)
    {
        await using var connection = await fixture.OpenRawAsync();
        await ExecuteAsync(connection, Albumen.Infrastructure.Sqlite.Review.OverrideEvidenceSchema.CreateTableSql);
        await ExecuteAsync(connection, Albumen.Infrastructure.Sqlite.Action.ActionJournalSchema.CreateItemsSql);
        await ExecuteAsync(connection, Albumen.Infrastructure.Sqlite.Action.ActionJournalSchema.CreateTransitionsSql);
        await ExecuteAsync(connection, Albumen.Infrastructure.Sqlite.Action.ActionRevalidationSchema.CreateTableSql);
        await ExecuteAsync(connection, """
            INSERT INTO override_evidence VALUES ('evidence-1','2026-09-14T10:00:00.0000000Z','review-1','scan-legacy',2,3,4,'group-1',5,6,'[1]','[]','policy-v1','text-v1',1,'override-hash');
            INSERT INTO action_items VALUES ('action-1','request-1','request-hash',7,8,'2026-09-14T10:01:00.0000000Z','requested-payload','requested-hash');
            INSERT INTO action_transitions VALUES ('action-1',2,'executing','2026-09-14T10:02:00.0000000Z','transition-payload','transition-hash');
            INSERT INTO action_revalidations VALUES ('revalidation-1','action-1',1,'2026-09-14T10:03:00.0000000Z','confirmed_present','original_file_confirmed','observation-payload','observation-hash');
            PRAGMA user_version=3;
            """);
        await AlbumenSchema.ValidateAsync(connection, 3, TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> EpicOneRowsAsync(SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (var table in new[] { "override_evidence", "action_items", "action_transitions", "action_revalidations" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table};";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(table + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }
        return [.. rows];
    }

    private static string ScanInsertSql(string id) => $"""
        INSERT INTO scans VALUES ('{id}',5,7,11,'C:\photos','C:\photos',1,
          '000000000000000d','0000000000000011','0000000000000013','2026-09-14T10:11:12.0000000Z');
        """;

    private static async Task<Dictionary<string, byte[]?>> SnapshotFilesAsync(string path)
    {
        var snapshot = new Dictionary<string, byte[]?>();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            snapshot[suffix] = File.Exists(path + suffix)
                ? await File.ReadAllBytesAsync(path + suffix, TestContext.Current.CancellationToken)
                : null;
        return snapshot;
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        Assert.True(condition());
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private sealed class CountingRecycleBinExecutor : IRecycleBinExecutor
    {
        internal int Executions { get; private set; }
        public ValueTask<RecycleBinResult> ExecuteAsync(
            RecycleBinRequest request, CancellationToken cancellationToken)
        {
            Executions++;
            return ValueTask.FromResult(new RecycleBinResult(
                RecycleBinOutcomeKind.NotRemoved, ActionSafetyReason.CannotRecycle,
                new(0, 0, false, null, null, 0, 0, null, false, false, true)));
        }
    }
}
