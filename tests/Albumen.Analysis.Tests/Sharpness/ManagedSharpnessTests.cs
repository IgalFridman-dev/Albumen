using Albumen.Analysis.Sharpness;
using Xunit;

namespace Albumen.Analysis.Tests.Sharpness;

public sealed class ManagedSharpnessTests
{
    [Fact]
    public void ScoreIsDeterministicAndIgnoresQuarterTurns()
    {
        var pixels = Texture(48, 32);

        var first = ManagedSharpness.Measure(48, 32, pixels);
        var second = ManagedSharpness.Measure(48, 32, pixels);
        var rotated = ManagedSharpness.Measure(32, 48, RotateClockwise(pixels, 48, 32));

        Assert.True(first > 0);
        Assert.Equal(first, second);
        Assert.Equal(first, rotated);
    }

    [Fact]
    public void BlurLowersTheScoreAndStrongBlurLowersItFarMore()
    {
        var sharp = Texture(64, 48);
        var soft = BoxBlur(sharp, 64, 48, radius: 1);
        var blurry = BoxBlur(sharp, 64, 48, radius: 3);

        var sharpScore = ManagedSharpness.Measure(64, 48, sharp);
        var softScore = ManagedSharpness.Measure(64, 48, soft);
        var blurryScore = ManagedSharpness.Measure(64, 48, blurry);

        Assert.True(softScore < sharpScore);
        Assert.True(blurryScore * 4 < sharpScore);
        Assert.True(blurryScore < softScore);
    }

    [Fact]
    public void ASharpSubjectCoveringAFifthOfABlurredPhotoStillScoresAsSharp()
    {
        var sharp = Texture(64, 64);
        var blurry = BoxBlur(sharp, 64, 64, radius: 3);
        var portrait = WithSharpSquare(blurry, sharp, from: 16, size: 32);
        var speck = WithSharpSquare(blurry, sharp, from: 28, size: 8);

        var sharpScore = ManagedSharpness.Measure(64, 64, sharp);

        Assert.True(ManagedSharpness.Measure(64, 64, portrait) * 2 > sharpScore);
        Assert.True(ManagedSharpness.Measure(64, 64, speck) * 4 < sharpScore);
        Assert.True(ManagedSharpness.Measure(64, 64, blurry) * 4 < sharpScore);
    }

    [Fact]
    public void FlatOrTinyImagesHaveNoDetail()
    {
        Assert.Equal(0, ManagedSharpness.Measure(16, 16, Solid(16, 16, 90)));
        Assert.Equal(0, ManagedSharpness.Measure(2, 5, Texture(2, 5)));
    }

    [Fact]
    public void PixelBufferMustMatchDimensions()
    {
        Assert.Throws<ArgumentException>(() => ManagedSharpness.Measure(4, 4, new byte[4 * 4 * 4 - 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagedSharpness.Measure(0, 4, []));
    }

    // Deterministic pseudo-random grey texture: plenty of fine detail.
    private static byte[] Texture(int width, int height)
    {
        var random = new Random(20261019);
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var value = (byte)random.Next(256);
            pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Solid(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
    {
        var output = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            int total = 0, count = 0;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                var sx = Math.Clamp(x + dx, 0, width - 1);
                var sy = Math.Clamp(y + dy, 0, height - 1);
                total += source[(sy * width + sx) * 4 + channel];
                count++;
            }
            output[(y * width + x) * 4 + channel] = (byte)((total + count / 2) / count);
        }
        return output;
    }

    // A 64-pixel-wide copy of background with a sharp size×size square starting at (from, from).
    private static byte[] WithSharpSquare(byte[] background, byte[] sharp, int from, int size)
    {
        var output = (byte[])background.Clone();
        for (var y = from; y < from + size; y++)
            sharp.AsSpan((y * 64 + from) * 4, size * 4).CopyTo(output.AsSpan((y * 64 + from) * 4));
        return output;
    }

    private static byte[] RotateClockwise(byte[] source, int width, int height)
    {
        var output = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            source.AsSpan((y * width + x) * 4, 4).CopyTo(output.AsSpan((x * height + (height - 1 - y)) * 4));
        return output;
    }
}
