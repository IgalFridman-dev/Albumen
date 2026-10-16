using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Analysis.NearDuplicate;

public sealed class ManagedPerceptualSignature : IPerceptualSignatureAnalyzer
{
    public const string ImplementationRevision = "managed-dhash-bgra-v1";

    public static PerceptualSignature Create(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length != checked(width * height * 4))
            throw new ArgumentException("BGRA pixels must exactly match the supplied dimensions.", nameof(bgra));

        Span<byte> sample = stackalloc byte[9 * 8];
        long blue = 0, green = 0, red = 0, luma = 0;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 9; x++)
            sample[y * 9 + x] = SampleLuma(width, height, bgra, x, y);

        var pixelCount = checked(width * height);
        for (var index = 0; index < bgra.Length; index += 4)
        {
            blue += bgra[index];
            green += bgra[index + 1];
            red += bgra[index + 2];
            luma += Luma(bgra[index], bgra[index + 1], bgra[index + 2]);
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++, bit++)
            if (sample[y * 9 + x] < sample[y * 9 + x + 1]) hash |= 1UL << bit;

        return new(ImplementationRevision, hash, Average(luma, pixelCount),
            Average(blue, pixelCount), Average(green, pixelCount), Average(red, pixelCount));
    }

    public PerceptualSignature Extract(int width, int height, ReadOnlySpan<byte> bgra) =>
        Create(width, height, bgra);

    private static byte SampleLuma(int width, int height, ReadOnlySpan<byte> pixels, int x, int y)
    {
        var left = x * width / 9;
        var right = Math.Max(left + 1, (x + 1) * width / 9);
        var top = y * height / 8;
        var bottom = Math.Max(top + 1, (y + 1) * height / 8);
        long total = 0;
        var count = 0;
        for (var sourceY = top; sourceY < Math.Min(bottom, height); sourceY++)
        for (var sourceX = left; sourceX < Math.Min(right, width); sourceX++)
        {
            var offset = checked((sourceY * width + sourceX) * 4);
            total += Luma(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
            count++;
        }
        return Average(total, count);
    }

    private static int Luma(byte blue, byte green, byte red) =>
        (red * 77 + green * 150 + blue * 29 + 128) >> 8;

    private static byte Average(long total, int count) =>
        checked((byte)((total + count / 2) / count));
}
