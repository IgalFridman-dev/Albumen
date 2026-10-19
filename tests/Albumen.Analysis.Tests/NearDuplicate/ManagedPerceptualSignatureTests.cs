using Albumen.Analysis.NearDuplicate;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Analysis.Tests.NearDuplicate;

public sealed class ManagedPerceptualSignatureTests
{
    [Fact]
    public void SignatureIsDeterministicAndUsesOrientedBgraDimensions()
    {
        var pixels = Gradient(12, 8);

        var first = ManagedPerceptualSignature.Create(12, 8, pixels);
        var second = ManagedPerceptualSignature.Create(12, 8, pixels);
        var rotated = ManagedPerceptualSignature.Create(8, 12, RotateClockwise(pixels, 12, 8));

        Assert.Equal(ManagedPerceptualSignature.ImplementationRevision, first.ImplementationRevision);
        Assert.Equal(first, second);
        Assert.NotEqual(first.DifferenceHash, rotated.DifferenceHash);
    }

    [Fact]
    public void VersionedCorpusKeepsStrongEditsSeparateFromAmbiguityAndUnrelatedContent()
    {
        var original = Gradient(24, 16);
        var edit = Brighten(original, 8);
        var burst = Shift(original, 1);
        var resize = ResizeNearest(original, 24, 16, 12, 8);
        var recompression = Quantize(original, 16);
        var crop = Crop(original, 24, 16, 0, 0, 16, 16);
        var mirror = Mirror(original, 24, 16);
        var rotation = RotateClockwise(original, 24, 16);
        var unrelated = Checkerboard(24, 16);
        var edgeVertical = Edge(24, 16, vertical: true);
        var edgeHorizontal = Edge(24, 16, vertical: false);

        var source = ManagedPerceptualSignature.Create(24, 16, original);
        var editRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(24, 16, edit));
        var burstRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(24, 16, burst));
        var resizeRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(12, 8, resize));
        var recompressionRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(24, 16, recompression));
        var cropRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(16, 16, crop));
        var mirrorRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(24, 16, mirror));
        var rotationRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(16, 24, rotation));
        var unrelatedRelationship = NearDuplicateThresholds.Classify(source,
            ManagedPerceptualSignature.Create(24, 16, unrelated));
        var edgeRelationship = NearDuplicateThresholds.Classify(
            ManagedPerceptualSignature.Create(24, 16, edgeVertical),
            ManagedPerceptualSignature.Create(24, 16, edgeHorizontal));

        Assert.Equal(NearRelationshipKind.Near, editRelationship.Kind);
        Assert.Equal(NearRelationshipKind.Near, burstRelationship.Kind);
        Assert.Equal(NearRelationshipKind.Uncertain, resizeRelationship.Kind);
        Assert.Equal(NearRelationshipKind.Near, recompressionRelationship.Kind);
        Assert.NotEqual(NearRelationshipKind.Near, cropRelationship.Kind);
        Assert.NotEqual(NearRelationshipKind.Near, mirrorRelationship.Kind);
        Assert.NotEqual(NearRelationshipKind.Near, rotationRelationship.Kind);
        Assert.Equal(NearRelationshipKind.NotRelated, unrelatedRelationship.Kind);
        Assert.NotEqual(NearRelationshipKind.Near, edgeRelationship.Kind);
    }

    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)((x * 9 + y * 5) % 220);
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(value / 2);
            pixels[offset + 1] = value;
            pixels[offset + 2] = (byte)Math.Min(255, value + 20);
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Brighten(byte[] source, byte amount)
    {
        var copy = source.ToArray();
        for (var index = 0; index < copy.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            copy[index + channel] = (byte)Math.Min(255, copy[index + channel] + amount);
        return copy;
    }

    private static byte[] Shift(byte[] source, int pixels)
    {
        var copy = new byte[source.Length];
        var width = 24;
        var height = 16;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + Math.Max(0, x - pixels)) * 4,
                copy, (y * width + x) * 4, 4);
        return copy;
    }

    private static byte[] Mirror(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + (width - 1 - x)) * 4, result, (y * width + x) * 4, 4);
        return result;
    }

    private static byte[] RotateClockwise(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + x) * 4,
                result, (x * height + (height - 1 - y)) * 4, 4);
        return result;
    }

    private static byte[] Checkerboard(int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(((x / 2 + y / 2) & 1) == 0 ? 10 : 245);
            var offset = (y * width + x) * 4;
            result[offset] = result[offset + 1] = result[offset + 2] = value;
            result[offset + 3] = 255;
        }
        return result;
    }

    private static byte[] ResizeNearest(byte[] source, int width, int height, int outputWidth, int outputHeight)
    {
        var result = new byte[outputWidth * outputHeight * 4];
        for (var y = 0; y < outputHeight; y++)
        for (var x = 0; x < outputWidth; x++)
            Array.Copy(source, ((y * height / outputHeight) * width + x * width / outputWidth) * 4,
                result, (y * outputWidth + x) * 4, 4);
        return result;
    }

    private static byte[] Quantize(byte[] source, int step)
    {
        var result = source.ToArray();
        for (var index = 0; index < result.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            result[index + channel] = (byte)(result[index + channel] / step * step);
        return result;
    }

    private static byte[] Crop(byte[] source, int width, int height, int left, int top, int cropWidth, int cropHeight)
    {
        var result = new byte[cropWidth * cropHeight * 4];
        for (var y = 0; y < cropHeight; y++)
        for (var x = 0; x < cropWidth; x++)
            Array.Copy(source, (((top + y) * width) + left + x) * 4,
                result, (y * cropWidth + x) * 4, 4);
        return result;
    }

    private static byte[] Edge(int width, int height, bool vertical)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var bright = vertical ? x >= width / 2 : y >= height / 2;
            var value = (byte)(bright ? 240 : 15);
            var offset = (y * width + x) * 4;
            result[offset] = result[offset + 1] = result[offset + 2] = value;
            result[offset + 3] = 255;
        }
        return result;
    }
}
