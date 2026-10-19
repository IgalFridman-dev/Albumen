using Albumen.Analysis.Exact;
using Albumen.Application.Scan;
using Xunit;

namespace Albumen.Analysis.Tests.Exact;

public sealed class StreamingExactContentAnalyzerTests
{
    [Fact]
    public async Task HashesTheAbcVectorWithoutOwningTheStream()
    {
        var analyzer = new StreamingExactContentAnalyzer();
        using var stream = new MemoryStream("abc"u8.ToArray());

        var digest = await analyzer.HashAsync(stream, 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, digest.ByteLength);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", digest.Sha256);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(0, "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData(1, "4A64A107F0CB32536E5BCE6C98C393DB21CCA7F4EA187BA8C4DCA8B51D4EA80A")]
    [InlineData(65535, "AC10511FD718A56212BA55F035985EDD04C513D5942D1A43B6900287EFE3C4E4")]
    [InlineData(65536, "B2797A7998E01B1D4C7E9C0A31A0E1D3982028655EAA53615F50E24ECE188773")]
    [InlineData(65537, "AAC39BC984EDD2E2A1DA3A8BC4C2F7C097F9F188655BAF876D6106E6E799B5A9")]
    [InlineData(196731, "AE49524117F8A77BAF2F348BDA41FCCC9677D5B0DF4619DFA16E4A4D8A385C61")]
    public async Task HashesBufferBoundariesAndMultiBufferContent(int length, string expectedSha256)
    {
        var content = GenerateContent(length);
        using var stream = new AdversarialReadStream(content);

        var digest = await new StreamingExactContentAnalyzer().HashAsync(
            stream,
            length,
            TestContext.Current.CancellationToken);

        Assert.Equal(new StreamDigest(length, expectedSha256), digest);
    }

    [Fact]
    public async Task HashFillsAcrossShortReadsAndBoundsEveryRequest()
    {
        var content = GenerateContent(StreamingExactContentAnalyzer.BufferSize * 2 + 29);
        using var stream = new AdversarialReadStream(content, [1, 7, 31, 4093]);

        var digest = await new StreamingExactContentAnalyzer().HashAsync(
            stream,
            content.LongLength,
            TestContext.Current.CancellationToken);

        Assert.Equal("E94E59C6492C3D3DC8AA5C3A9861F1A2550B1014C2B010A0FC2DDBEA201ACAE5", digest.Sha256);
        Assert.InRange(stream.MaximumRequestedCount, 1, StreamingExactContentAnalyzer.BufferSize);
        Assert.Equal(content.LongLength, stream.BytesReturned);
    }

    [Fact]
    public async Task CompareHandlesAsymmetricShortReads()
    {
        var content = GenerateContent(StreamingExactContentAnalyzer.BufferSize + 117);
        using var left = new AdversarialReadStream(content, [1, 2, 3, 5, 8]);
        using var right = new AdversarialReadStream(content, [4097, 13, 2]);

        var result = await new StreamingExactContentAnalyzer().CompareAsync(
            left,
            right,
            content.LongLength,
            TestContext.Current.CancellationToken);

        Assert.True(result.Equal);
        Assert.Equal(result.Left, result.Right);
        Assert.Equal(content.LongLength, left.BytesReturned);
        Assert.Equal(content.LongLength, right.BytesReturned);
    }

    [Fact]
    public async Task CompareHashesBothCompleteStreamsAfterFindingDifferentBytes()
    {
        var leftContent = GenerateContent(StreamingExactContentAnalyzer.BufferSize + 19);
        var rightContent = leftContent.ToArray();
        rightContent[0] ^= 0xFF;
        using var left = new AdversarialReadStream(leftContent, [17]);
        using var right = new AdversarialReadStream(rightContent, [3, 1024]);

        var result = await new StreamingExactContentAnalyzer().CompareAsync(
            left,
            right,
            leftContent.LongLength,
            TestContext.Current.CancellationToken);

        Assert.False(result.Equal);
        Assert.Equal("3701E7CA0629115604FA47B6F55725A374DD7501CFB2AFFE9E8823C3326095B9", result.Left.Sha256);
        Assert.Equal("BDC8F88E9C0ADD17E3A56EA3F99FC74F0EE3429B564DF95731775A02FD17DA74", result.Right.Sha256);
        Assert.Equal(leftContent.LongLength, left.BytesReturned);
        Assert.Equal(rightContent.LongLength, right.BytesReturned);
        Assert.InRange(left.MaximumRequestedCount, 1, StreamingExactContentAnalyzer.BufferSize);
        Assert.InRange(right.MaximumRequestedCount, 1, StreamingExactContentAnalyzer.BufferSize);
    }

    [Fact]
    public async Task CompareDoesNotOwnEitherStream()
    {
        var content = "same"u8.ToArray();
        using var left = new AdversarialReadStream(content);
        using var right = new AdversarialReadStream(content);

        var result = await new StreamingExactContentAnalyzer().CompareAsync(
            left,
            right,
            content.LongLength,
            TestContext.Current.CancellationToken);

        Assert.True(result.Equal);
        Assert.False(left.WasDisposed);
        Assert.False(right.WasDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HashRejectsUnexpectedEndOrExtraByte(bool endsEarly)
    {
        var content = endsEarly ? "ab"u8.ToArray() : "abcd"u8.ToArray();
        using var stream = new AdversarialReadStream(content, [1]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await new StreamingExactContentAnalyzer().HashAsync(
                stream,
                3,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompareRejectsUnexpectedEndOrExtraByte(bool endsEarly)
    {
        var invalid = endsEarly ? "ab"u8.ToArray() : "abcd"u8.ToArray();
        using var left = new AdversarialReadStream("abc"u8.ToArray(), [2]);
        using var right = new AdversarialReadStream(invalid, [1]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await new StreamingExactContentAnalyzer().CompareAsync(
                left,
                right,
                3,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HashPropagatesReadFailures()
    {
        using var stream = new AdversarialReadStream(GenerateContent(100), [9], failAtPosition: 27);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await new StreamingExactContentAnalyzer().HashAsync(
                stream,
                100,
                TestContext.Current.CancellationToken));

        Assert.Equal("Injected read failure.", exception.Message);
    }

    [Fact]
    public async Task ComparePropagatesReadFailures()
    {
        using var left = new AdversarialReadStream(GenerateContent(100), [7]);
        using var right = new AdversarialReadStream(GenerateContent(100), [5], failAtPosition: 25);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await new StreamingExactContentAnalyzer().CompareAsync(left, right, 100, TestContext.Current.CancellationToken));

        Assert.Equal("Injected read failure.", exception.Message);
    }

    [Fact]
    public async Task HashObservesCancellationDuringRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancellationReadStream();

        var operation = new StreamingExactContentAnalyzer().HashAsync(stream, 1, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task CompareObservesCancellationDuringRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var left = new CancellationReadStream();
        using var right = new AdversarialReadStream([1]);

        var operation = new StreamingExactContentAnalyzer().CompareAsync(left, right, 1, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task CollisionLabelsCannotTurnDifferentBytesIntoEquality()
    {
        const string forcedDigest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        IExactContentAnalyzer analyzer = new CollidingDigestAnalyzer(new StreamingExactContentAnalyzer(), forcedDigest);
        using var leftSignatureStream = new MemoryStream("left"u8.ToArray());
        using var rightSignatureStream = new MemoryStream("rift"u8.ToArray());
        using var leftComparisonStream = new MemoryStream("left"u8.ToArray());
        using var rightComparisonStream = new MemoryStream("rift"u8.ToArray());

        var leftSignature = await analyzer.HashAsync(
            leftSignatureStream,
            4,
            TestContext.Current.CancellationToken);
        var rightSignature = await analyzer.HashAsync(
            rightSignatureStream,
            4,
            TestContext.Current.CancellationToken);
        var result = await analyzer.CompareAsync(
            leftComparisonStream,
            rightComparisonStream,
            4,
            TestContext.Current.CancellationToken);

        Assert.Equal(leftSignature, rightSignature);
        Assert.False(result.Equal);
        Assert.Equal(forcedDigest, result.Left.Sha256);
        Assert.Equal(forcedDigest, result.Right.Sha256);
    }

    private static byte[] GenerateContent(int length)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = checked((byte)(((long)index * 31 + 17) % 251));
        }

        return content;
    }

    private sealed class CollidingDigestAnalyzer(IExactContentAnalyzer inner, string forcedDigest)
        : IExactContentAnalyzer
    {
        public async ValueTask<StreamDigest> HashAsync(
            Stream content,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            var actual = await inner.HashAsync(content, expectedLength, cancellationToken);
            return actual with { Sha256 = forcedDigest };
        }

        public async ValueTask<StreamEquality> CompareAsync(
            Stream left,
            Stream right,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            var actual = await inner.CompareAsync(left, right, expectedLength, cancellationToken);
            return actual with
            {
                Left = actual.Left with { Sha256 = forcedDigest },
                Right = actual.Right with { Sha256 = forcedDigest },
            };
        }
    }
}
