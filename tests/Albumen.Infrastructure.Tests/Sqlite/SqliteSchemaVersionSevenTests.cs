using System.Globalization;
using Microsoft.Data.Sqlite;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionFiveTests;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

/// <summary>Every 1.1 database is at version six: its facts and vectors must survive the signature table.</summary>
public sealed class SqliteSchemaVersionSevenTests
{
    private static readonly EncoderProfile Profile = new(
        "siglip2-test", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 4, 1, 100, -10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task VersionSixFactsAndVectorsSurviveTheVersionSevenExtension()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await SeedVersionSixAsync(fixture, scan);
        string before;
        await using (var versionSix = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await DowngradeToVersionSixAsync(versionSix);
            await AlbumenSchema.ValidateAsync(versionSix, 6, Token);
            before = await RowsAsync(versionSix);
        }

        await using (var migrated = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            var vectors = new List<StoredEmbedding>();
            await migrated.Embeddings.ReadAsync(scan.Pin.ScanId, Profile, vectors.Add, Token);
            Assert.Equal([0.5f, -0.5f, 0.5f, -0.5f], Assert.Single(vectors).Vector);
        }

        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(7L, await ScalarAsync(verify, "PRAGMA user_version;"));
        Assert.Equal(before, await RowsAsync(verify));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT count(*) FROM signatures;"));
    }

    // Source six runs three steps: the signature table, the version, the validation.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryVersionSevenMigrationStepFailureLeavesVersionSixUnchanged(int faultStep)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await SeedVersionSixAsync(fixture, scan);
        string before;
        await using (var versionSix = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await DowngradeToVersionSixAsync(versionSix);
            before = await DatabaseSnapshotAsync(versionSix);
        }
        var observer = new SqliteDatabaseObserver(MigrationStep: (step, _, _) =>
            step == faultStep
                ? ValueTask.FromException(new IOException("Injected version-seven migration failure."))
                : ValueTask.CompletedTask);

        await using (var rejected = new SqliteAlbumenStores(fixture.DatabasePath, observer))
            Assert.Equal(ScanIdentityReadOutcome.Unavailable,
                (await rejected.ScanIdentities.ReadScanAsync(scan.Pin.ScanId, Token)).Outcome);

        await using var verify = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        Assert.Equal(before, await DatabaseSnapshotAsync(verify));
    }

    private static async Task SeedVersionSixAsync(ScanIdentityFixture fixture, ScanIdentityRecord scan)
    {
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistScanAsync(scan, Token)).Outcome);
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistOccurrenceAsync(ScanIdentityData.Occurrence(scan.Pin, 7), Token)).Outcome);
            await stores.Embeddings.AddAsync(scan.Pin.ScanId, Profile,
                [new StoredEmbedding(7, new string('D', 64), [0.5f, -0.5f, 0.5f, -0.5f])], Token);
        }
        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite);
        await SeedEpicOneFactsAsync(raw);
    }

    private static Task DowngradeToVersionSixAsync(SqliteConnection connection) => ExecuteAsync(connection, """
        DROP TABLE signatures;
        PRAGMA user_version=6;
        PRAGMA journal_mode=DELETE;
        """);

    // Every stored row of the version-six tables.
    private static async Task<string> RowsAsync(SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (var table in new[]
                 {
                     "action_items", "action_revalidations", "action_transitions", "override_evidence",
                     "scan_accounting", "scan_embeddings", "scan_file_occurrences", "scan_lifecycle",
                     "scan_stage_checkpoints", "scans",
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
                rows.Add(table + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? "<null>" :
                        Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }
        return string.Join('\n', rows);
    }
}
