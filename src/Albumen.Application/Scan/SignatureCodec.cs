using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

/// <summary>
/// The stored form of each feature. A value that does not decode is a reject: the feature is
/// computed again, so a damaged row can never reach a group or a score.
/// </summary>
public static class SignatureCodec
{
    private const int NearLength = 12;

    // The dHash as a little-endian ulong, then the luma, blue, green and red averages.
    public static byte[] EncodeNear(PerceptualSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var value = new byte[NearLength];
        BinaryPrimitives.WriteUInt64LittleEndian(value, signature.DifferenceHash);
        value[8] = signature.AverageLuma;
        value[9] = signature.AverageBlue;
        value[10] = signature.AverageGreen;
        value[11] = signature.AverageRed;
        return value;
    }

    public static bool TryDecodeNear(byte[]? value, string revision,
        [NotNullWhen(true)] out PerceptualSignature? signature)
    {
        signature = value is { Length: NearLength }
            ? new(revision, BinaryPrimitives.ReadUInt64LittleEndian(value), value[8], value[9], value[10], value[11])
            : null;
        return signature is not null;
    }

    // Null is "no score" (the photo is smaller than the sharpness frame), stored so it is reused too.
    public static byte[]? EncodeSharpness(double? score)
    {
        if (score is not { } measured) return null;
        var value = new byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(value, measured);
        return value;
    }

    public static bool TryDecodeSharpness(byte[]? value, out double? score)
    {
        score = null;
        if (value is null) return true;
        if (value.Length != sizeof(double)) return false;
        var measured = BinaryPrimitives.ReadDoubleLittleEndian(value);
        if (!double.IsFinite(measured) || measured < 0) return false;
        score = measured;
        return true;
    }

    // Float32 in the machine's order (win-x64: little-endian), as scan_embeddings stores vectors.
    public static byte[] EncodeVector(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
    }

    public static bool TryDecodeVector(byte[]? value, int dimension, [NotNullWhen(true)] out float[]? vector)
    {
        vector = value is not null && value.Length == dimension * sizeof(float)
            ? MemoryMarshal.Cast<byte, float>(value).ToArray()
            : null;
        return vector is not null;
    }
}
