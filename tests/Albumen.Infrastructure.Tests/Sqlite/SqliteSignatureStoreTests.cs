using Microsoft.Data.Sqlite;
using Albumen.Application.LocalData;
using Albumen.Application.Scan;
using Albumen.Infrastructure.Sqlite;
using Xunit;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteSignatureStoreTests
{
    private static readonly string Sha = new('D', 64);
    private static readonly SignatureKey Near = new(Sha, 10, SignatureKind.Near, "near-v1");
    private static readonly SignatureKey Sharpness = new(Sha, 10, SignatureKind.Sharpness, "sharpness-v1");
    private static readonly SignatureKey Vector = new(Sha, 10, SignatureKind.Vector, new string('f', 64));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoredValuesAreFoundByTheirFullKeyAndAbsentKeysAreMisses()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.Signatures.AddAsync(
            [new(Near, Bytes(12, 1)), new(Sharpness, null), new(Vector, Bytes(16, 3))], Token);
        var absent = Near with { Sha256 = new string('E', 64) };

        var found = await stores.Signatures.LookupAsync([Near, Sharpness, Vector, absent], Token);

        Assert.Equal(3, found.Count);
        Assert.Equal(Bytes(12, 1), found[Near]);
        Assert.True(found.ContainsKey(Sharpness));
        Assert.Null(found[Sharpness]);
        Assert.Equal(Bytes(16, 3), found[Vector]);
        Assert.False(found.ContainsKey(absent));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("kind")]
    [InlineData("revision")]
    [InlineData("sha256")]
    public async Task AKeyThatDiffersInAnyPartIsAMiss(string part)
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.Signatures.AddAsync([new(Sharpness, Bytes(8, 1))], Token);
        var other = part switch
        {
            "length" => Sharpness with { ByteLength = 11 },
            "kind" => Sharpness with { Kind = SignatureKind.Near },
            "revision" => Sharpness with { Revision = "sharpness-v2" },
            _ => Sharpness with { Sha256 = new string('E', 64) },
        };

        Assert.Empty(await stores.Signatures.LookupAsync([other], Token));
    }

    [Fact]
    public async Task AKeyAlreadyStoredKeepsItsFirstValue()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.Signatures.AddAsync([new(Near, Bytes(12, 1))], Token);

        await stores.Signatures.AddAsync([new(Near, Bytes(12, 2)), new(Sharpness, Bytes(8, 2))], Token);

        var found = await stores.Signatures.LookupAsync([Near, Sharpness], Token);
        Assert.Equal(Bytes(12, 1), found[Near]);
        Assert.Equal(Bytes(8, 2), found[Sharpness]);
    }

    [Fact]
    public async Task SignaturesNeedNoScanAndOutliveTheStores()
    {
        await using var fixture = new ScanIdentityFixture();
        await using (var first = new SqliteAlbumenStores(fixture.DatabasePath))
            await first.Signatures.AddAsync([new(Near, Bytes(12, 1))], Token);

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(Bytes(12, 1), (await reopened.Signatures.LookupAsync([Near], Token))[Near]);
    }

    [Theory]
    [InlineData(SignatureKind.Near, 11)]
    [InlineData(SignatureKind.Near, -1)]
    [InlineData(SignatureKind.Sharpness, 9)]
    [InlineData(SignatureKind.Vector, 6)]
    [InlineData(SignatureKind.Vector, -1)]
    public async Task AValueOfTheWrongShapeIsRefusedAndTheBatchKeepsNothing(SignatureKind kind, int length)
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var bad = new StoredSignature(new(Sha, 10, kind, "bad"), length < 0 ? null : Bytes(length, 1));

        await Assert.ThrowsAsync<SqliteException>(() =>
            stores.Signatures.AddAsync([new(Near, Bytes(12, 1)), bad], Token).AsTask());

        Assert.Empty(await stores.Signatures.LookupAsync([Near, bad.Key], Token));
    }

    [Fact]
    public async Task ClearingLocalScanDataRemovesTheSignatures()
    {
        await using var fixture = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.Signatures.AddAsync([new(Near, Bytes(12, 1))], Token);

        Assert.Equal(ScanDataClearOutcome.Cleared, (await stores.ScanData.ClearAsync(Token)).Outcome);

        Assert.Empty(await stores.Signatures.LookupAsync([Near], Token));
    }

    private static byte[] Bytes(int length, byte fill) => Enumerable.Repeat(fill, length).ToArray();
}
