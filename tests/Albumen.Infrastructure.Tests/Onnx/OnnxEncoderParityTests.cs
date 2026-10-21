using System.Text.Json.Nodes;
using Albumen.Infrastructure.Onnx;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace Albumen.Infrastructure.Tests.Onnx;

/// <summary>
/// The shipped model files against the PyTorch reference written by eng/models/export_siglip2.py.
/// The files are not in git, so this is a Golden VM test: it reads them from ALBUMEN_MODELS_DIR, or
/// from C:\PhotoSift\models where the VM stages them for the release build, and the folder must also
/// hold the shipped models/manifest.json.
/// </summary>
[Trait("Category", "GoldenVm")]
public sealed class OnnxEncoderParityTests
{
    // Same pixels and fp32 weights: only kernel rounding differs. The text vocabulary is stored in 16 bits.
    private const double MinimumImageCosine = 0.9999;
    private const double MinimumTextCosine = 0.999;

    [Fact]
    public void ShippedModelMatchesThePyTorchReference()
    {
        var models = Environment.GetEnvironmentVariable("ALBUMEN_MODELS_DIR") is { Length: > 0 } configured
            ? configured
            : @"C:\PhotoSift\models";
        Assert.True(Directory.Exists(models), $"The model files are not staged at {models}.");
        Assert.Equal(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models", "manifest.json")),
            File.ReadAllText(Path.Combine(models, "manifest.json")));
        var reference = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Onnx", "siglip2-reference.json")))!;
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;
        using var stream = File.OpenRead(Path.Combine(models, "tokenizer.model"));
        var tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);

        foreach (var (image, index) in reference["images"]!.AsArray().Select((image, index) => (image!, index)))
        {
            var result = encoder.EncodeImage(
                profile,
                OnnxVisionLanguageEncoderTests.SyntheticImage(index, (int)image["width"]!, (int)image["height"]!));
            Assert.InRange(Cosine(result.Vector.ToArray(), image["embedding"]!), MinimumImageCosine, 1.0001);
        }

        foreach (var prompt in reference["prompts"]!.AsArray())
        {
            var text = (string)prompt!["text"]!;
            Assert.Equal(
                prompt["ids"]!.AsArray().Select(id => (long)id!),
                SiglipPreprocessing.TokenIds(tokenizer, text, profile.MaxTokens, profile.PadId));
            Assert.InRange(Cosine(encoder.EncodeText(profile, text).Vector.ToArray(), prompt["embedding"]!), MinimumTextCosine, 1.0001);
        }
    }

    private static double Cosine(float[] actual, JsonNode expected)
    {
        var reference = expected.AsArray().Select(v => (double)v!).ToArray();
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            dot += actual[i] * reference[i];
            a += (double)actual[i] * actual[i];
            b += reference[i] * reference[i];
        }

        return dot / Math.Sqrt(a * b);
    }
}
