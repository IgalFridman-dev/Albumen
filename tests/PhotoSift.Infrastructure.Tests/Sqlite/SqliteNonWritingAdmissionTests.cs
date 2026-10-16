using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Review;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteNonWritingAdmissionTests
{
    [Fact]
    public async Task OwnerReaderCannotAcquireMutationAuthority()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var owner = new SqliteDatabaseOwner(fixture.DatabasePath);

        var error = await owner.RunReadAsync(async (connection, token) =>
            await Assert.ThrowsAsync<SqliteException>(() =>
                SqliteDatabaseOwner.ExecuteAsync(connection, "PRAGMA user_version=99;", token)),
            TestContext.Current.CancellationToken);

        Assert.Equal(8, error.SqliteErrorCode);
    }

    [Fact]
    public async Task RejectedRetainedCommittedWalCannotCheckpointOrChangeAnySidecar()
    {
        await using var donor = new SqliteOverrideFixture();
        await using var rejected = new SqliteOverrideFixture();
        await SeedAsync(donor, 2);
        await using (var connection = await donor.OpenRawAsync())
        {
            Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode=WAL;"));
            await ExecuteAsync(connection, "PRAGMA wal_autocheckpoint=0; CREATE TABLE foreign_object(value TEXT); INSERT INTO foreign_object VALUES ('committed-in-wal');");
            var bytes = await SnapshotAsync(donor.DatabasePath);
            var wal = Assert.IsType<byte[]>(bytes["-wal"]);
            Assert.True(wal.Length > 32);
            var pageSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8, 4)));
            Assert.Contains(Enumerable.Range(0, (wal.Length - 32) / (pageSize + 24)), frame =>
                BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(32 + frame * (pageSize + 24) + 4, 4)) > 0);
            await CopySnapshotAsync(bytes, rejected.DatabasePath);
        }
        await RejectAndRetainAsync(rejected, expectedHeaderMode: 2);
    }

    [Fact]
    public async Task RejectedHotRollbackJournalCannotBeRecoveredByAdmission()
    {
        await using var donor = new SqliteOverrideFixture();
        await using var rejected = new SqliteOverrideFixture();
        await using (var connection = await donor.OpenRawAsync())
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode=DELETE; CREATE TABLE retained(value BLOB); WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000) INSERT INTO retained SELECT zeroblob(2000) FROM n; PRAGMA user_version=99; PRAGMA cache_size=5; PRAGMA cache_spill=ON;");
            Assert.Equal("delete", await ScalarAsync(connection, "PRAGMA journal_mode;"));
            await using var transaction = connection.BeginTransaction(deferred: false);
            await ExecuteAsync(connection, "UPDATE retained SET value=zeroblob(3000);", transaction);
            var bytes = await SnapshotAsync(donor.DatabasePath);
            var journal = Assert.IsType<byte[]>(bytes["-journal"]);
            Assert.Equal(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }, journal[..8]);
            await CopySnapshotAsync(bytes, rejected.DatabasePath);
        }
        // Prove that this is real recovery-required state, not a clean DELETE
        // close. A read-only connection must refuse the hot journal.
        await using (var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = rejected.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await readOnly.OpenAsync(TestContext.Current.CancellationToken);
            var failure = await Assert.ThrowsAsync<SqliteException>(() => ScalarAsync(readOnly, "PRAGMA user_version;"));
            Assert.Equal(8, failure.SqliteErrorCode);
        }
        await RejectAndRetainAsync(rejected, expectedHeaderMode: 1);
    }

    [Fact]
    public async Task RejectedSidecarFreeWalModeCannotCreateWalOrSharedMemoryFiles()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var connection = await fixture.OpenRawAsync())
        {
            Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode=WAL;"));
            await ExecuteAsync(connection, "CREATE TABLE foreign_object(value TEXT); PRAGMA user_version=99;");
        }
        Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
        await RejectAndRetainAsync(fixture, expectedHeaderMode: 2);
    }

    [Theory]
    [InlineData("-wal")] [InlineData("-shm")] [InlineData("-journal")]
    public async Task EvenEmptyRetainedSidecarsRefuseAdmissionWithoutRemoval(string sidecar)
    {
        await using var fixture = new SqliteOverrideFixture();
        await SeedAsync(fixture, 1);
        await File.WriteAllBytesAsync(fixture.DatabasePath + sidecar, [], TestContext.Current.CancellationToken);
        await RejectAndRetainAsync(fixture, expectedHeaderMode: 1);
    }

    private static async Task RejectAndRetainAsync(SqliteOverrideFixture fixture, byte expectedHeaderMode)
    {
        var before = await SnapshotAsync(fixture.DatabasePath);
        Assert.Equal(expectedHeaderMode, before[""]![18]);
        Assert.Equal(expectedHeaderMode, before[""]![19]);
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
            Assert.Equal(OverrideReadOutcome.Unavailable,
                (await stores.OverrideEvidence.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken)).Outcome);
        var after = await SnapshotAsync(fixture.DatabasePath);
        foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
        // Journal mode was read from the live donor before its snapshot. These
        // retained header bytes prove no mode change without a mutating reopen.
        Assert.Equal(expectedHeaderMode, after[""]![18]);
        Assert.Equal(expectedHeaderMode, after[""]![19]);
    }

    private static async Task<Dictionary<string, byte[]?>> SnapshotAsync(string path)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (!File.Exists(path + suffix)) { result.Add(suffix, null); continue; }
            await using var file = new FileStream(path + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, TestContext.Current.CancellationToken);
            result.Add(suffix, buffer.ToArray());
        }
        return result;
    }
    private static async Task CopySnapshotAsync(Dictionary<string, byte[]?> snapshot, string destination)
    {
        foreach (var pair in snapshot)
            if (pair.Value is { } bytes) await File.WriteAllBytesAsync(destination + pair.Key, bytes, TestContext.Current.CancellationToken);
    }
}
