using Microsoft.Data.Sqlite;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Sqlite;
using Xunit;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteScanIdentityStoreTests
{
    [Fact]
    public async Task ExactRetriesAreIdempotentAndConflictsNeverOverwrite()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scan = ScanIdentityData.Scan();
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 7);
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        Assert.Equal(ScanIdentityPersistOutcome.ExactRetry,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        Assert.Equal(ScanIdentityPersistOutcome.Conflict,
            (await stores.ScanIdentities.PersistScanAsync(scan with { ResolvedPath = @"C:\contradiction" }, token)).Outcome);

        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistOccurrenceAsync(occurrence, token)).Outcome);
        Assert.Equal(ScanIdentityPersistOutcome.ExactRetry,
            (await stores.ScanIdentities.PersistOccurrenceAsync(occurrence, token)).Outcome);
        Assert.Equal(ScanIdentityPersistOutcome.Conflict,
            (await stores.ScanIdentities.PersistOccurrenceAsync(
                occurrence with { Token = occurrence.Token with
                {
                    Pin = occurrence.Token.Pin with { ScopeRevision = 99 },
                } }, token)).Outcome);
        Assert.Equal(ScanIdentityPersistOutcome.Conflict,
            (await stores.ScanIdentities.PersistOccurrenceAsync(
                occurrence with { DiscoveredPath = @"C:\contradiction.jpg" }, token)).Outcome);

        var scanRead = await stores.ScanIdentities.ReadScanAsync(scan.Pin.ScanId, token);
        var occurrenceRead = await stores.ScanIdentities.ReadOccurrencesAsync(scan.Pin.ScanId, token);
        Assert.Equal(ScanIdentityReadOutcome.Found, scanRead.Outcome);
        Assert.Equal(scan, scanRead.Record);
        Assert.Equal(ScanIdentityReadOutcome.Found, occurrenceRead.Outcome);
        Assert.Equal([occurrence], occurrenceRead.Records);
    }

    [Fact]
    public async Task MissingScanForeignKeyRejectsOccurrence()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);

        var result = await stores.ScanIdentities.PersistOccurrenceAsync(
            ScanIdentityData.Occurrence(ScanIdentityData.Scan().Pin, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(ScanIdentityPersistOutcome.MissingScan, result.Outcome);
        Assert.Empty((await stores.ScanIdentities.ReadOccurrencesAsync(
            ScanIdentityData.Scan().Pin.ScanId, TestContext.Current.CancellationToken)).Records);
    }

    [Fact]
    public async Task CanonicalValuesAndUnicodePathsRoundTripWithoutLosingUnsignedBits()
    {
        await using var fixture = new ScanIdentityFixture();
        var longSegment = new string('ש', 300);
        var scan = ScanIdentityData.Scan(
            selectedPath: $@"C:\фото\{longSegment}",
            resolvedPath: $@"C:\фото\{longSegment}\.",
            root: new(ulong.MaxValue, 0x8000000000000000UL, 0xfedcba9876543210UL));
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 9,
            discoveredPath: scan.Selection.SelectedPath + @"\猫.jpg",
            resolvedPath: scan.ResolvedPath + @"\猫.jpg",
            physical: new(0x8000000000000001UL, new(ulong.MaxValue, 0xabcdef0123456789UL)));

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

        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        await using var command = raw.CreateCommand();
        command.CommandText = """
            SELECT scan_id, created_utc, root_volume_serial, root_file_id_high, root_file_id_low
            FROM scans;
            """;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("f15c9f48-2069-4ee5-9a18-fd92d885627b", reader.GetString(0));
        Assert.Equal("2026-09-14T10:11:12.0000000Z", reader.GetString(1));
        Assert.Equal("ffffffffffffffff", reader.GetString(2));
        Assert.Equal("8000000000000000", reader.GetString(3));
        Assert.Equal("fedcba9876543210", reader.GetString(4));
    }

    [Fact]
    public async Task NullPhysicalIdentityUsesOnlyAnAllNullTuple()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, 1, physical: null);
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken);
            await stores.ScanIdentities.PersistOccurrenceAsync(occurrence, TestContext.Current.CancellationToken);
            Assert.Equal([occurrence], (await stores.ScanIdentities.ReadOccurrencesAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Records);
        }

        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite);
        await using var query = raw.CreateCommand();
        query.CommandText = "SELECT volume_serial, file_id_high, file_id_low FROM scan_file_occurrences;";
        await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        await reader.DisposeAsync();

        await using var corrupt = raw.CreateCommand();
        corrupt.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE scan_file_occurrences SET volume_serial='0000000000000001';";
        await corrupt.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await raw.DisposeAsync();
        SqliteConnection.ClearAllPools();

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ScanIdentityReadOutcome.Unavailable,
            (await reopened.ScanIdentities.ReadOccurrencesAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task NoncanonicalPersistedTextFailsClosedWithoutNormalization()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
            await stores.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken);

        await using (var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await using var mutate = raw.CreateCommand();
            mutate.CommandText = """
                UPDATE scans
                SET root_volume_serial = '000000000000000D',
                    created_utc = '2026-09-14T10:11:12.0000000+00:00';
                """;
            await mutate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        SqliteConnection.ClearAllPools();

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ScanIdentityReadOutcome.Corrupt,
            (await reopened.ScanIdentities.ReadScanAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task OccurrenceReadsAreOrderedAndDoNotMutateDatabase()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ScanIdentities.PersistScanAsync(scan, TestContext.Current.CancellationToken);
            foreach (var id in new long[] { 20, 3, 11 })
                await stores.ScanIdentities.PersistOccurrenceAsync(
                    ScanIdentityData.Occurrence(scan.Pin, id), TestContext.Current.CancellationToken);
        }
        SqliteConnection.ClearAllPools();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);
        ScanOccurrenceIdentityReadResult read;
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
            read = await stores.ScanIdentities.ReadOccurrencesAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken);
        SqliteConnection.ClearAllPools();
        var after = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);

        Assert.Equal(ScanIdentityReadOutcome.Found, read.Outcome);
        Assert.Equal([3L, 11L, 20L], read.Records.Select(value => value.Token.OccurrenceId));
        Assert.Equal(before, after);
    }
}

internal sealed class ScanIdentityFixture : IAsyncDisposable
{
    internal ScanIdentityFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "Albumen-ScanIdentity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        DatabasePath = Path.Combine(RootPath, "albumen.db");
    }

    internal string RootPath { get; }
    internal string DatabasePath { get; }

    internal async Task<SqliteConnection> OpenRawAsync(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
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

internal static class ScanIdentityData
{
    internal static ScanIdentityRecord Scan(
        string selectedPath = @"C:\photos",
        string resolvedPath = @"C:\photos",
        DirectoryIdentity? root = null) => new(
            new(Guid.Parse("f15c9f48-2069-4ee5-9a18-fd92d885627b"), 5, 7, 11),
            new(selectedPath, ScopeRecursion.IncludeSubfolders), resolvedPath,
            root ?? new(13, 17, 19),
            new DateTimeOffset(2026, 9, 14, 10, 11, 12, TimeSpan.Zero));

    internal static ScanOccurrenceIdentityRecord Occurrence(
        ScanPin pin,
        long occurrenceId,
        string discoveredPath = @"C:\photos\image.jpg",
        string resolvedPath = @"C:\photos\image.jpg",
        PhysicalFileKey? physical = null) => new(
            new(pin, occurrenceId, 2, 3), discoveredPath, resolvedPath,
            physical);
}
