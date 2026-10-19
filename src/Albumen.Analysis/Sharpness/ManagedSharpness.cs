using System.Buffers;
using Albumen.Application.Scan;

namespace Albumen.Analysis.Sharpness;

// Variance of the 4-neighbour Laplacian of luma: focus and motion blur remove the fine detail
// it responds to. The score only means something next to scores of the same scene measured at
// the same frame size, which is how Review uses it (within one set).
public sealed class ManagedSharpness : ISharpnessAnalyzer
{
    public static double Measure(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var pixelCount = checked(width * height);
        if (bgra.Length != checked(pixelCount * 4))
            throw new ArgumentException("BGRA pixels must exactly match the supplied dimensions.", nameof(bgra));
        if (width < 3 || height < 3) return 0;

        var luma = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            for (var index = 0; index < pixelCount; index++)
                luma[index] = (byte)((bgra[index * 4 + 2] * 77 + bgra[index * 4 + 1] * 150
                    + bgra[index * 4] * 29 + 128) >> 8);

            // Integer sums keep the score identical on every machine.
            long sum = 0, sumOfSquares = 0;
            for (var y = 1; y < height - 1; y++)
            for (var x = 1; x < width - 1; x++)
            {
                var at = y * width + x;
                var laplacian = luma[at - 1] + luma[at + 1] + luma[at - width] + luma[at + width] - 4 * luma[at];
                sum += laplacian;
                sumOfSquares += laplacian * laplacian;
            }
            var count = (double)(width - 2) * (height - 2);
            var mean = sum / count;
            return Math.Max(0, sumOfSquares / count - mean * mean);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(luma);
        }
    }

    public double Analyze(int width, int height, ReadOnlySpan<byte> bgra) => Measure(width, height, bgra);
}
