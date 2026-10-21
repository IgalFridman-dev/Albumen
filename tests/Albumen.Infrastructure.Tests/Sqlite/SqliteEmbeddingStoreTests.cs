using Microsoft.Data.Sqlite;
using Albumen.Application.LocalData;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Infrastructure.Sqlite;
using Xunit;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteEmbeddingStoreTests
{
    private static readonly EncoderProfile Profile = new(
        "siglip2-test", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 4, 1);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task VectorsReadBackBitExactInOccurrenceOrderForTheirScanAndProfile()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scanId = await PersistScanAsync(stores, 7, 3);

        await stores.Embeddings.AddAsync(scanId, Profile,
            [Embedding(7, 0.5f, -0.5f, 0.5f, -0.5f), Embedding(3, 1f, 0f, 0f, float.Epsilon)], Token);

        var read = await ReadAsync(stores, scanId, Profile);
        Assert.Equal([3L, 7L], read.Select(embedding => embedding.OccurrenceId));
        Assert.Equal([1f, 0f, 0f, float.Epsilon], read[0].Vector);
        Assert.Equal([0.5f, -0.5f, 0.5f, -0.5f], read[1].Vector);
        Assert.Equal(new string('D', 64), read[0].Sha256);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("vision")]
    [InlineData("pipeline")]
    [InlineData("scan")]
    public async Task VectorsOfAnotherProfileOrScanAreNeverRead(string mismatch)
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scanId = await PersistScanAsync(stores, 1);
        await stores.Embeddings.AddAsync(scanId, Profile, [Embedding(1, 1f, 0f, 0f, 0f)], Token);

        var (readScan, readProfile) = mismatch switch
        {
            "model" => (scanId, Profile with { Model = "siglip2-other" }),
            "vision" => (scanId, Profile with { VisionSha256 = new string('E', 64) }),
            "pipeline" => (scanId, Profile with { PipelineRevision = 2 }),
            _ => (Guid.NewGuid(), Profile),
        };

        Assert.Empty(await ReadAsync(stores, readScan, readProfile));
    }

    [Fact]
    public async Task ClearRemovesEveryVectorAndKeepsTheScan()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scanId = await PersistScanAsync(stores, 1, 2);
        await stores.Embeddings.AddAsync(scanId, Profile,
            [Embedding(1, 1f, 0f, 0f, 0f), Embedding(2, 0f, 1f, 0f, 0f)], Token);

        await stores.Embeddings.ClearAsync(Token);

        Assert.Empty(await ReadAsync(stores, scanId, Profile));
        var occurrences = await stores.ScanIdentities.ReadOccurrencesAsync(scanId, Token);
        Assert.Equal(ScanIdentityReadOutcome.Found, occurrences.Outcome);
        Assert.Equal(2, occurrences.Records.Length);
    }

    [Fact]
    public async Task ClearingLocalScanDataRemovesTheVectors()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scanId = await PersistScanAsync(stores, 1);
        await stores.Embeddings.AddAsync(scanId, Profile, [Embedding(1, 1f, 0f, 0f, 0f)], Token);

        Assert.Equal(ScanDataClearOutcome.Cleared, (await stores.ScanData.ClearAsync(Token)).Outcome);

        Assert.Empty(await ReadAsync(stores, scanId, Profile));
    }

    [Fact]
    public async Task AVectorOfTheWrongDimensionIsRefusedOnWriteAndFailsTheRead()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scanId = await PersistScanAsync(stores, 1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            stores.Embeddings.AddAsync(scanId, Profile, [Embedding(1, 1f, 0f, 0f)], Token).AsTask());

        await stores.Embeddings.AddAsync(scanId, Profile, [Embedding(1, 1f, 0f, 0f, 0f)], Token);
        await using (var connection = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE scan_embeddings SET vector = substr(vector, 1, 8);";
            await command.ExecuteNonQueryAsync(Token);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(stores, scanId, Profile));
    }

    [Fact]
    public async Task AVectorNeedsAPersistedOccurrenceAndIsWrittenOnce()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var scanId = await PersistScanAsync(stores, 1);

        await Assert.ThrowsAsync<SqliteException>(() =>
            stores.Embeddings.AddAsync(scanId, Profile, [Embedding(2, 1f, 0f, 0f, 0f)], Token).AsTask());
        await stores.Embeddings.AddAsync(scanId, Profile, [Embedding(1, 1f, 0f, 0f, 0f)], Token);
        await Assert.ThrowsAsync<SqliteException>(() =>
            stores.Embeddings.AddAsync(scanId, Profile, [Embedding(1, 0f, 1f, 0f, 0f)], Token).AsTask());

        var read = Assert.Single(await ReadAsync(stores, scanId, Profile));
        Assert.Equal([1f, 0f, 0f, 0f], read.Vector);
    }

    private static StoredEmbedding Embedding(long occurrenceId, params float[] vector) =>
        new(occurrenceId, new string('D', 64), vector);

    private static async Task<Guid> PersistScanAsync(SqliteAlbumenStores stores, params long[] occurrenceIds)
    {
        var scan = ScanIdentityData.Scan();
        Assert.Equal(ScanIdentityPersistOutcome.Persisted, (await stores.ScanIdentities.PersistScanAsync(scan, Token)).Outcome);
        foreach (var id in occurrenceIds)
        {
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistOccurrenceAsync(ScanIdentityData.Occurrence(scan.Pin, id), Token)).Outcome);
        }

        return scan.Pin.ScanId;
    }

    private static async Task<List<StoredEmbedding>> ReadAsync(
        SqliteAlbumenStores stores, Guid scanId, EncoderProfile profile)
    {
        var read = new List<StoredEmbedding>();
        await stores.Embeddings.ReadAsync(scanId, profile, read.Add, Token);
        return read;
    }
}
