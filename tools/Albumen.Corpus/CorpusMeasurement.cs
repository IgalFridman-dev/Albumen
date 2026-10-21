using Albumen.Analysis.NearDuplicate;
using Albumen.Analysis.Sharpness;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Onnx;
using Albumen.Infrastructure.Windows.Preview;

namespace Albumen.Corpus;

internal sealed record CorpusFeatures(PerceptualSignature Signature, double? Sharpness, float[] Embedding);

// What a Scan computes for one photo, with the same decodes and the same code: the hash on the
// 256-edge decode, sharpness on the 1,024-edge decode (none for smaller photos), and the image
// embedding on the encoder's 512-edge decode.
internal static class CorpusMeasurement
{
    private static readonly WicPreviewDecoder Decoder = new();

    public static async Task<CorpusFeatures> MeasureAsync(string path, IVisionLanguageEncoder encoder, EncoderProfile profile)
    {
        using var near = await DecodeAsync(path, WicNearFeatureSource.MaximumEdge).ConfigureAwait(false);
        using var detail = await DecodeAsync(path, WicNearFeatureSource.SharpnessEdge).ConfigureAwait(false);
        using var semantic = await DecodeAsync(path, OnnxVisionLanguageEncoder.DecodeEdge).ConfigureAwait(false);

        var signature = ManagedPerceptualSignature.Create(near.Width, near.Height, near.AnalysisPixels.Span);
        double? sharpness = Math.Max(detail.Width, detail.Height) == WicNearFeatureSource.SharpnessEdge
            ? ManagedSharpness.Measure(detail.Width, detail.Height, detail.AnalysisPixels.Span)
            : null;
        var embedding = encoder.EncodeImage(profile, new EncoderImage(semantic.AnalysisPixels, semantic.Width, semantic.Height));
        if (embedding.Failure is { } failure)
            throw new InvalidDataException($"{path}: {failure.Reason}");
        return new(signature, sharpness, embedding.Vector.ToArray());
    }

    private static async Task<PreviewFrame> DecodeAsync(string path, int maximumEdge)
    {
        using var result = await Decoder.DecodeAsync(path, maximumEdge).ConfigureAwait(false);
        return result.Frame is null
            ? throw new InvalidDataException($"{path}: {result.Reason}")
            : result.TakeFrame();
    }
}
