using Microsoft.ML.Tokenizers;

namespace Albumen.Infrastructure.Onnx;

/// <summary>The model inputs, built exactly as <c>eng/models/export_siglip2.py</c> builds its reference.</summary>
internal static class SiglipPreprocessing
{
    private const int PrecisionBits = 32 - 8 - 2;

    /// <summary>
    /// BGRA pixels to a [3, size, size] tensor in [-1, 1]: Pillow's antialiased bilinear resize
    /// (horizontal pass, then vertical, 8-bit fixed point), squashed to a square.
    /// </summary>
    public static float[] Pixels(ReadOnlySpan<byte> bgra, int width, int height, int size)
    {
        var (xStarts, xWeights) = Coefficients(width, size);
        var horizontal = new byte[height * size * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < size; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 1 << (PrecisionBits - 1);
                    var weights = xWeights[x];
                    for (var i = 0; i < weights.Length; i++)
                    {
                        sum += bgra[((y * width) + xStarts[x] + i) * 4 + 2 - c] * weights[i];
                    }

                    horizontal[((y * size) + x) * 3 + c] = Clip(sum);
                }
            }
        }

        var (yStarts, yWeights) = Coefficients(height, size);
        var tensor = new float[3 * size * size];
        for (var y = 0; y < size; y++)
        {
            var weights = yWeights[y];
            for (var x = 0; x < size; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var sum = 1 << (PrecisionBits - 1);
                    for (var i = 0; i < weights.Length; i++)
                    {
                        sum += horizontal[(((yStarts[y] + i) * size) + x) * 3 + c] * weights[i];
                    }

                    tensor[(c * size * size) + (y * size) + x] = (Clip(sum) / 255f - 0.5f) / 0.5f;
                }
            }
        }

        return tensor;
    }

    /// <summary>Lower-cased ids, truncated so the end-of-sentence id always stays, then right-padded.</summary>
    public static long[] TokenIds(SentencePieceTokenizer tokenizer, string prompt, int maxTokens, int padId)
    {
        var ids = tokenizer.EncodeToIds(prompt.ToLowerInvariant(), addBeginningOfSentence: false, addEndOfSentence: false);
        var result = new long[maxTokens];
        Array.Fill(result, padId);
        var count = Math.Min(ids.Count, maxTokens - 1);
        for (var i = 0; i < count; i++)
        {
            result[i] = ids[i];
        }

        result[count] = tokenizer.EndOfSentenceId;
        return result;
    }

    private static byte Clip(int sum) =>
        sum <= 0 ? (byte)0 : sum >= (255 << PrecisionBits) ? (byte)255 : (byte)(sum >> PrecisionBits);

    // Pillow precompute_coeffs + normalize_coeffs_8bpc for the bilinear (triangle) filter.
    private static (int[] Starts, int[][] Weights) Coefficients(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var support = Math.Max(scale, 1.0);
        var starts = new int[outSize];
        var weights = new int[outSize][];
        for (var o = 0; o < outSize; o++)
        {
            var center = (o + 0.5) * scale;
            var min = Math.Max((int)(center - support + 0.5), 0);
            var count = Math.Min((int)(center + support + 0.5), inSize) - min;
            var k = new double[count];
            var total = 0.0;
            for (var i = 0; i < count; i++)
            {
                total += k[i] = Math.Max(0, 1 - Math.Abs((i + min - center + 0.5) / support));
            }

            starts[o] = min;
            weights[o] = [.. k.Select(w => (int)(w / total * (1 << PrecisionBits) + (w < 0 ? -0.5 : 0.5)))];
        }

        return (starts, weights);
    }
}
