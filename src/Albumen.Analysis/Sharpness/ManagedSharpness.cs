using System.Buffers;
using Albumen.Application.Scan;

namespace Albumen.Analysis.Sharpness;

// Variance of the 4-neighbour Laplacian of luma: focus and motion blur remove the fine detail
// it responds to. It is measured per tile of an 8×8 grid and the score is the mean of the
// sharpest fifth of the tiles: a sharp subject covering about 20% of a deliberately blurred
// photo (portrait mode) still scores as sharp, while a few sharp pixels in a blurry photo do
// not. The score only means something next to
// scores of the same scene at the same frame size, which is how Review uses it (within a set).
public sealed class ManagedSharpness : ISharpnessAnalyzer
{
    private const int Grid = 8;
    private const int SharpestTiles = 13; // of 64, about a fifth of the photo

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
            Span<long> sums = stackalloc long[Grid * Grid];
            Span<long> squares = stackalloc long[Grid * Grid];
            Span<long> counts = stackalloc long[Grid * Grid];
            for (var y = 1; y < height - 1; y++)
            {
                var row = y * Grid / height * Grid;
                for (var x = 1; x < width - 1; x++)
                {
                    var at = y * width + x;
                    var laplacian = luma[at - 1] + luma[at + 1] + luma[at - width] + luma[at + width] - 4 * luma[at];
                    var tile = row + x * Grid / width;
                    sums[tile] += laplacian;
                    squares[tile] += laplacian * laplacian;
                    counts[tile]++;
                }
            }

            var variances = new List<double>(Grid * Grid);
            for (var tile = 0; tile < Grid * Grid; tile++)
            {
                if (counts[tile] == 0) continue;
                var mean = (double)sums[tile] / counts[tile];
                variances.Add(Math.Max(0, (double)squares[tile] / counts[tile] - mean * mean));
            }
            variances.Sort();
            return variances.TakeLast(SharpestTiles).Average();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(luma);
        }
    }

    public double Analyze(int width, int height, ReadOnlySpan<byte> bgra) => Measure(width, height, bgra);
}
