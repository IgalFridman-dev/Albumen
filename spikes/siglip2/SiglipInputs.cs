using Microsoft.ML.Tokenizers;

namespace Siglip2Spike;

/// <summary>Model inputs built the way export.py builds the reference.</summary>
public static class SiglipInputs
{
    private const int PrecisionBits = 32 - 8 - 2;

    /// <summary>
    /// BGRA pixels to a [3, size, size] tensor in [-1, 1]: a port of Pillow's antialiased bilinear
    /// resize (horizontal pass, then vertical, 8-bit fixed point), squashed to a square like the reference.
    /// </summary>
    public static float[] Pixels(byte[] bgra, int width, int height, int size)
    {
        var horizontal = new byte[size * height * 3];
        var (xBounds, xWeights) = Coefficients(width, size);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < size; x++)
                for (var c = 0; c < 3; c++)
                    horizontal[((y * size) + x) * 3 + c] = Sample(
                        xBounds[x], xWeights[x], i => bgra[((y * width) + i) * 4 + 2 - c]);

        var tensor = new float[3 * size * size];
        var (yBounds, yWeights) = Coefficients(height, size);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                for (var c = 0; c < 3; c++)
                {
                    var value = Sample(yBounds[y], yWeights[y], i => horizontal[((i * size) + x) * 3 + c]);
                    tensor[(c * size * size) + (y * size) + x] = (value / 255f - 0.5f) / 0.5f;
                }
        return tensor;
    }

    /// <summary>Lower-cased SentencePiece ids, truncated or padded to the trained length.</summary>
    public static long[] TokenIds(SentencePieceTokenizer tokenizer, string text, int maxLength, int padId, bool padLeft)
    {
        var ids = tokenizer.EncodeToIds(text.ToLowerInvariant()).Take(maxLength).Select(id => (long)id).ToArray();
        var result = Enumerable.Repeat((long)padId, maxLength).ToArray();
        ids.CopyTo(result, padLeft ? maxLength - ids.Length : 0);
        return result;
    }

    private static byte Sample(int start, int[] weights, Func<int, byte> source)
    {
        var sum = 1 << (PrecisionBits - 1);
        for (var i = 0; i < weights.Length; i++)
            sum += source(start + i) * weights[i];
        return sum <= 0 ? (byte)0 : sum >= (255 << PrecisionBits) ? (byte)255 : (byte)(sum >> PrecisionBits);
    }

    // Pillow precompute_coeffs + normalize_coeffs_8bpc for the bilinear (triangle) filter.
    private static (int[] Starts, int[][] Weights) Coefficients(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = filterScale;
        var starts = new int[outSize];
        var weights = new int[outSize][];
        for (var o = 0; o < outSize; o++)
        {
            var center = (o + 0.5) * scale;
            var min = Math.Max((int)(center - support + 0.5), 0);
            var max = Math.Min((int)(center + support + 0.5), inSize) - min;
            var k = new double[max];
            var total = 0.0;
            for (var i = 0; i < max; i++)
                total += k[i] = Math.Max(0, 1 - Math.Abs((i + min - center + 0.5) / filterScale));
            starts[o] = min;
            weights[o] = k.Select(w => (int)(w / total * (1 << PrecisionBits) + (w < 0 ? -0.5 : 0.5))).ToArray();
        }
        return (starts, weights);
    }
}
