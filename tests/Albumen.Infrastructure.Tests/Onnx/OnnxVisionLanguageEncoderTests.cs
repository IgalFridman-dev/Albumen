using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Albumen.Application.Semantic;
using Albumen.Infrastructure.Onnx;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace Albumen.Infrastructure.Tests.Onnx;

/// <summary>The adapter against tiny stand-in model files (Fixtures/make_fixtures.py); the real model is in OnnxEncoderParityTests.</summary>
public sealed class OnnxVisionLanguageEncoderTests : IDisposable
{
    private static readonly string FixturesPath = Path.Combine(AppContext.BaseDirectory, "Onnx", "Fixtures");
    private readonly string models = Directory.CreateTempSubdirectory("albumen-models-").FullName;

    public OnnxVisionLanguageEncoderTests()
    {
        foreach (var name in new[] { "manifest.json", "vision.onnx", "text.onnx", "tokenizer.model" })
        {
            File.Copy(Path.Combine(FixturesPath, name), Path.Combine(models, name));
        }
    }

    public void Dispose() => Directory.Delete(models, recursive: true);

    [Fact]
    public void InstalledFilesGiveTheirProfile()
    {
        using var encoder = new OnnxVisionLanguageEncoder(models);

        var profile = encoder.LoadProfile().Profile!;

        Assert.Equal("fixture/tiny", profile.Model);
        Assert.Equal(OnnxVisionLanguageEncoder.DecodeEdge, profile.DecodeEdge);
        Assert.Equal((224, 8, 0, 4), (profile.ImageSize, profile.MaxTokens, profile.PadId, profile.Dimension));
        Assert.Equal(Sha256("vision.onnx"), profile.VisionSha256);
    }

    [Fact]
    public void ImageVectorIsTheModelOutputNormalized()
    {
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;
        var image = SyntheticImage(1, 300, 200);

        var result = encoder.EncodeImage(profile, image);

        // The stand-in vision model is [mean R, mean G, mean B, sum of the three].
        var pixels = SiglipPreprocessing.Pixels(image.Bgra.Span, image.Width, image.Height, 224);
        var plane = 224 * 224;
        var means = Enumerable.Range(0, 3).Select(c => pixels.Skip(c * plane).Take(plane).Average(v => (double)v)).ToArray();
        AssertUnitVectorAlong([means[0], means[1], means[2], means.Sum()], result);
    }

    [Fact]
    public void TextVectorIsTheModelOutputOfTheTokenIdsNormalized()
    {
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;

        var result = encoder.EncodeText(profile, "A Dog");

        // The stand-in text model multiplies the ids by w[i, j] = i * 4 + j + 1; "a dog" is [4, 37, 28, 52, eos 1, pad 0...].
        long[] ids = [4, 37, 28, 52, 1, 0, 0, 0];
        var expected = Enumerable.Range(0, 4).Select(j => ids.Select((id, i) => id * (i * 4.0 + j + 1)).Sum()).ToArray();
        AssertUnitVectorAlong(expected, result);
    }

    [Fact]
    public void TokenIdsMatchSentencePieceKeepingEndOfSentenceWhenTruncated()
    {
        using var stream = File.OpenRead(Path.Combine(FixturesPath, "tokenizer.model"));
        var tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);

        foreach (var prompt in Expected()["prompts"]!.AsArray())
        {
            Assert.Equal(
                prompt!["ids"]!.AsArray().Select(id => (long)id!),
                SiglipPreprocessing.TokenIds(tokenizer, (string)prompt["text"]!, maxTokens: 8, padId: 0));
        }
    }

    [Fact]
    public void ResizeMatchesPillowBilinearByteForByte()
    {
        foreach (var resize in Expected()["resize"]!.AsArray())
        {
            var size = (int)resize!["size"]!;
            var image = SyntheticImage((int)resize["index"]!, (int)resize["width"]!, (int)resize["height"]!);
            var rgb = resize["rgb"]!.AsArray().Select(v => (int)v!).ToArray();

            var pixels = SiglipPreprocessing.Pixels(image.Bgra.Span, image.Width, image.Height, size);

            var expected = Enumerable.Range(0, 3 * size * size)
                .Select(i => (rgb[(i % (size * size) * 3) + (i / (size * size))] / 255f - 0.5f) / 0.5f);
            Assert.Equal(expected, pixels);
        }
    }

    [Fact]
    public void MissingManifestIsUnavailable()
    {
        File.Delete(Path.Combine(models, "manifest.json"));
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Equal(EncoderFailureKind.Unavailable, encoder.LoadProfile().Failure!.Kind);
    }

    [Fact]
    public void MissingModelFileIsUnavailable()
    {
        File.Delete(Path.Combine(models, "text.onnx"));
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Equal(EncoderFailureKind.Unavailable, encoder.LoadProfile().Failure!.Kind);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("dimension", "0")]
    [InlineData("license", "null")]
    [InlineData("logitScale", "0")]
    [InlineData("unexpected", "1")]
    public void InvalidManifestFails(string property, string json)
    {
        EditManifest(manifest => manifest[property] = JsonNode.Parse(json));
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Equal(EncoderFailureKind.Failed, encoder.LoadProfile().Failure!.Kind);
    }

    [Fact]
    public void ManifestPathOutsideTheModelsFolderFails()
    {
        EditManifest(manifest => manifest["files"]!["text"]!["path"] = "../text.onnx");
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Equal(EncoderFailureKind.Failed, encoder.LoadProfile().Failure!.Kind);
    }

    [Fact]
    public void WrongFileSizeFails()
    {
        File.AppendAllText(Path.Combine(models, "vision.onnx"), " ");
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Equal(EncoderFailureKind.Failed, encoder.LoadProfile().Failure!.Kind);
    }

    [Fact]
    public void ChecksumMismatchFailsOnlyThatTower()
    {
        var path = Path.Combine(models, "vision.onnx");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;

        var image = encoder.EncodeImage(profile, SyntheticImage(0, 64, 48));
        var text = encoder.EncodeText(profile, "a dog");

        Assert.Equal(EncoderFailureKind.Failed, image.Failure!.Kind);
        Assert.Contains("checksum", image.Failure.Reason, StringComparison.Ordinal);
        Assert.Null(text.Failure);
    }

    [Fact]
    public void LockedModelFileIsUnavailable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows enforces share modes.");
        using var locked = new FileStream(Path.Combine(models, "vision.onnx"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Equal(EncoderFailureKind.Unavailable, encoder.LoadProfile().Failure!.Kind);
    }

    [Fact]
    public void CorruptTokenizerFailsOnlyText()
    {
        File.WriteAllText(Path.Combine(models, "tokenizer.model"), "not a sentencepiece model");
        EditManifest(manifest =>
        {
            manifest["files"]!["tokenizer"]!["length"] = new FileInfo(Path.Combine(models, "tokenizer.model")).Length;
            manifest["files"]!["tokenizer"]!["sha256"] = Sha256("tokenizer.model");
        });
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;

        Assert.Equal(EncoderFailureKind.Failed, encoder.EncodeText(profile, "a dog").Failure!.Kind);
        Assert.Null(encoder.EncodeImage(profile, SyntheticImage(0, 64, 48)).Failure);
    }

    // The Scan opens the text tower ahead of the first search. That encodes nothing, and a text
    // tower that cannot open gives the very failure the search's own encoding then reports.
    [Fact]
    public void PreparingTextOpensItsTowerAndKeepsItsFailureForTheSearch()
    {
        using (var encoder = new OnnxVisionLanguageEncoder(models))
        {
            var profile = encoder.LoadProfile().Profile!;
            Assert.Null(encoder.PrepareText(profile));
            Assert.Null(encoder.EncodeText(profile, "a dog").Failure);
            Assert.Equal(EncoderFailureKind.Failed, encoder.PrepareText(profile with { DecodeEdge = 256 })!.Kind);
        }

        File.WriteAllText(Path.Combine(models, "tokenizer.model"), "not a sentencepiece model");
        EditManifest(manifest =>
        {
            manifest["files"]!["tokenizer"]!["length"] = new FileInfo(Path.Combine(models, "tokenizer.model")).Length;
            manifest["files"]!["tokenizer"]!["sha256"] = Sha256("tokenizer.model");
        });
        using var broken = new OnnxVisionLanguageEncoder(models);
        var brokenProfile = broken.LoadProfile().Profile!;

        var failure = broken.PrepareText(brokenProfile);

        Assert.Equal(EncoderFailureKind.Failed, failure?.Kind);
        Assert.Equal(failure, broken.EncodeText(brokenProfile, "a dog").Failure);
        Assert.Null(broken.EncodeImage(brokenProfile, SyntheticImage(0, 64, 48)).Failure);
    }

    [Fact]
    public void UnexpectedOutputNameFails()
    {
        File.Copy(Path.Combine(FixturesPath, "vision-wrong-output.onnx"), Path.Combine(models, "vision.onnx"), overwrite: true);
        EditManifest(manifest =>
        {
            manifest["files"]!["vision"]!["length"] = new FileInfo(Path.Combine(models, "vision.onnx")).Length;
            manifest["files"]!["vision"]!["sha256"] = Sha256("vision.onnx");
        });
        using var encoder = new OnnxVisionLanguageEncoder(models);

        var result = encoder.EncodeImage(encoder.LoadProfile().Profile!, SyntheticImage(0, 64, 48));

        Assert.Equal(EncoderFailureKind.Failed, result.Failure!.Kind);
    }

    [Fact]
    public void WrongDimensionFails()
    {
        EditManifest(manifest => manifest["dimension"] = 768);
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;

        Assert.Equal(EncoderFailureKind.Failed, encoder.EncodeImage(profile, SyntheticImage(0, 64, 48)).Failure!.Kind);
        Assert.Equal(EncoderFailureKind.Failed, encoder.EncodeText(profile, "a dog").Failure!.Kind);
    }

    [Fact]
    public void AnotherProfileIsRejected()
    {
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var other = encoder.LoadProfile().Profile! with { DecodeEdge = 256 };

        Assert.Equal(EncoderFailureKind.Failed, encoder.EncodeImage(other, SyntheticImage(0, 64, 48)).Failure!.Kind);
        Assert.Equal(EncoderFailureKind.Failed, encoder.EncodeText(other, "a dog").Failure!.Kind);
    }

    [Fact]
    public void ImageLargerThanTheDecodeEdgeIsAProgrammingError()
    {
        using var encoder = new OnnxVisionLanguageEncoder(models);

        Assert.Throws<ArgumentException>(() =>
            encoder.EncodeImage(encoder.LoadProfile().Profile!, SyntheticImage(0, 513, 10)));
    }

    /// <summary>The formula shared with make_fixtures.py and eng/models/export_siglip2.py, as BGRA.</summary>
    internal static EncoderImage SyntheticImage(int index, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    bgra[offset + 2 - c] = (byte)(((x * 3) + (y * 5) + (c * 85) + (((x / 16) + (y / 16) + index) % 2 * 128)) % 256);
                }

                bgra[offset + 3] = 255;
            }
        }

        return new EncoderImage(bgra, width, height);
    }

    private static JsonNode Expected() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixturesPath, "expected.json")))!;

    private static void AssertUnitVectorAlong(double[] direction, EmbeddingResult result)
    {
        Assert.Null(result.Failure);
        var norm = Math.Sqrt(direction.Sum(v => v * v));
        var vector = result.Vector.ToArray();
        Assert.Equal(direction.Length, vector.Length);
        for (var i = 0; i < vector.Length; i++)
        {
            Assert.Equal(direction[i] / norm, vector[i], 1e-5);
        }
    }

    private string Sha256(string name) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(models, name))));

    private void EditManifest(Action<JsonNode> edit)
    {
        var path = Path.Combine(models, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        edit(manifest);
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
