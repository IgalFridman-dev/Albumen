using System.Runtime.InteropServices;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class SignatureCodecTests
{
    [Fact]
    public void ANearSignatureRoundTripsThroughTwelveLittleEndianBytes()
    {
        var signature = new PerceptualSignature("stored", 0x0123456789ABCDEF, 1, 2, 3, 4);

        var value = SignatureCodec.EncodeNear(signature);

        Assert.Equal([0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01, 1, 2, 3, 4], value);
        Assert.True(SignatureCodec.TryDecodeNear(value, "r", out var decoded));
        Assert.Equal(signature with { ImplementationRevision = "r" }, decoded);
    }

    [Fact]
    public void ASharpnessScoreRoundTripsAndNoScoreIsNull()
    {
        var value = SignatureCodec.EncodeSharpness(12.5);

        Assert.Equal(BitConverter.GetBytes(12.5), value);
        Assert.True(SignatureCodec.TryDecodeSharpness(value, out var score));
        Assert.Equal(12.5, score);
        Assert.Null(SignatureCodec.EncodeSharpness(null));
        Assert.True(SignatureCodec.TryDecodeSharpness(null, out var none));
        Assert.Null(none);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })]
    public void ANearValueOfAnotherLengthOrNoneIsRejected(byte[] value)
    {
        Assert.False(SignatureCodec.TryDecodeNear(value, "r", out var signature));
        Assert.Null(signature);
        Assert.False(SignatureCodec.TryDecodeNear(null, "r", out _));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void ASharpnessScoreThatCannotBeTrueIsRejected(double stored)
    {
        Assert.False(SignatureCodec.TryDecodeSharpness(BitConverter.GetBytes(stored), out _));
        Assert.False(SignatureCodec.TryDecodeSharpness([1, 2, 3, 4, 5, 6, 7], out _));
    }

    [Fact]
    public void AVectorRoundTripsAndTheWrongLengthIsRejected()
    {
        float[] vector = [0.5f, -0.5f, 0.5f, float.Epsilon];

        var value = SignatureCodec.EncodeVector(vector);

        Assert.Equal(MemoryMarshal.AsBytes(vector.AsSpan()).ToArray(), value);
        Assert.True(SignatureCodec.TryDecodeVector(value, 4, out var decoded));
        Assert.Equal(vector, decoded);
        Assert.False(SignatureCodec.TryDecodeVector(value[..12], 4, out _));
        Assert.False(SignatureCodec.TryDecodeVector(value, 3, out _));
        Assert.False(SignatureCodec.TryDecodeVector(null, 4, out _));
    }
}
