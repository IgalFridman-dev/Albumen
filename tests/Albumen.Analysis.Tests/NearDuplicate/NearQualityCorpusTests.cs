using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Albumen.Analysis.NearDuplicate;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Analysis.Tests.NearDuplicate;

public sealed class NearQualityCorpusTests
{
    private const string OwnedProvenance = "generated-project-owned";
    private static readonly ImmutableArray<string> RequiredTransformations =
    [
        "byte-identical",
        "brightness",
        "burst shift",
        "recompression",
        "resize ambiguity",
        "crop",
        "mirror",
        "rotation",
        "unrelated",
        "invalid pixel buffer",
    ];
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void VersionOneManifestDeclaresOwnedLabeledExpectationsAndFailureSlices()
    {
        var manifest = LoadManifest();

        Assert.Equal(1, manifest.Version);
        Assert.Equal(RequiredTransformations.Length, manifest.Cases.Length);
        Assert.Equal(RequiredTransformations,
            manifest.Cases.Select(item => item.Transformation).ToImmutableArray());
        Assert.Equal(manifest.Cases.Length,
            manifest.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(manifest.Cases, item =>
        {
            Assert.Equal(1, item.Version);
            Assert.Equal(OwnedProvenance, item.ConsentProvenance);
            Assert.False(string.IsNullOrWhiteSpace(item.FailureSlice));
        });
    }

    [Fact]
    public void VersionOneRunnerMatchesEveryIndependentManifestExpectation()
    {
        var manifest = LoadManifest();

        var observed = NearQualityCorpusRunner.Run(manifest.Cases);

        Assert.Equal(manifest.Cases.Length, observed.Count);
        foreach (var item in manifest.Cases)
        {
            var result = Assert.Contains(item.Id, observed);
            Assert.True(item.ExpectedRelationship == result.Relationship,
                $"{item.Id}: expected {item.ExpectedRelationship}, observed {result.Relationship}.");
            Assert.Equal(item.FailureSlice, result.FailureSlice);
        }
    }

    private static NearQualityManifest LoadManifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "NearDuplicate", "NearQualityCorpusManifest.json");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<NearQualityManifest>(stream, ManifestJson)
            ?? throw new InvalidDataException("The Near quality manifest is empty.");
    }
}

internal sealed record NearQualityManifest(
    int Version,
    ImmutableArray<NearQualityCase> Cases);

internal sealed record NearQualityCase(
    string Id,
    int Version,
    string ConsentProvenance,
    string Transformation,
    QualityExpectedRelationship ExpectedRelationship,
    string FailureSlice);

internal enum QualityExpectedRelationship
{
    Exact,
    Near,
    Uncertain,
    NotRelated,
    InputValidationFailure,
}

internal static class NearQualityCorpusRunner
{
    public static ImmutableDictionary<string, NearQualityObserved> Run(
        ImmutableArray<NearQualityCase> cases)
    {
        var results = ImmutableDictionary.CreateBuilder<string, NearQualityObserved>(
            StringComparer.Ordinal);
        foreach (var item in cases)
        {
            var pair = NearQualityCorpusRecipes.CreatePair(item.Transformation);
            QualityExpectedRelationship relationship;
            try
            {
                if (pair.Left.Width == pair.Right.Width
                    && pair.Left.Height == pair.Right.Height
                    && pair.Left.Pixels.AsSpan().SequenceEqual(pair.Right.Pixels))
                {
                    relationship = QualityExpectedRelationship.Exact;
                }
                else
                {
                    var left = ManagedPerceptualSignature.Create(
                        pair.Left.Width, pair.Left.Height, pair.Left.Pixels);
                    var right = ManagedPerceptualSignature.Create(
                        pair.Right.Width, pair.Right.Height, pair.Right.Pixels);
                    relationship = NearDuplicateThresholds.Classify(left, right).Kind switch
                    {
                        NearRelationshipKind.Near => QualityExpectedRelationship.Near,
                        NearRelationshipKind.Uncertain => QualityExpectedRelationship.Uncertain,
                        NearRelationshipKind.NotRelated => QualityExpectedRelationship.NotRelated,
                        _ => throw new InvalidOperationException("Unknown Near relationship."),
                    };
                }
            }
            catch (ArgumentException) when (item.Transformation == "invalid pixel buffer")
            {
                relationship = QualityExpectedRelationship.InputValidationFailure;
            }

            results.Add(item.Id, new(relationship, item.FailureSlice));
        }
        return results.ToImmutable();
    }
}

internal sealed record NearQualityObserved(
    QualityExpectedRelationship Relationship,
    string FailureSlice);

internal static class NearQualityCorpusRecipes
{
    public static NearQualityPair CreatePair(string transformation)
    {
        var source = Gradient(24, 16);
        return transformation switch
        {
            "byte-identical" => Pair(source, source.ToArray(), 24, 16),
            "brightness" => Pair(source, Brighten(source, 8), 24, 16),
            "burst shift" => Pair(source, Shift(source, 1), 24, 16),
            "recompression" => Pair(source, Quantize(source, 16), 24, 16),
            "resize ambiguity" => new(new(24, 16, source),
                new(12, 8, ResizeNearest(source, 24, 16, 12, 8))),
            "crop" => CropPair(),
            "mirror" => Pair(source, Mirror(source, 24, 16), 24, 16),
            "rotation" => new(new(24, 16, source),
                new(16, 24, RotateClockwise(source, 24, 16))),
            "unrelated" => Pair(source, Checkerboard(24, 16), 24, 16),
            "invalid pixel buffer" => new(new(24, 16, source), new(24, 16, [0x00])),
            _ => throw new InvalidDataException($"Unknown quality transformation: {transformation}"),
        };
    }

    private static NearQualityPair Pair(byte[] left, byte[] right, int width, int height) =>
        new(new(width, height, left), new(width, height, right));

    private static NearQualityPair CropPair()
    {
        var source = CropSource(24, 16);
        return new(new(24, 16, source), new(8, 8, Crop(source, 24, 8, 8)));
    }

    private static byte[] CropSource(int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(x < 8 && y < 8
                ? ((x + y) & 1) == 0 ? 10 : 245
                : 0);
            var offset = (y * width + x) * 4;
            result[offset] = result[offset + 1] = result[offset + 2] = value;
            result[offset + 3] = 255;
        }
        return result;
    }

    internal static byte[] Gradient(int width, int height)
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

    internal static byte[] Brighten(byte[] source, byte amount)
    {
        var result = source.ToArray();
        for (var index = 0; index < result.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            result[index + channel] = (byte)Math.Min(255, result[index + channel] + amount);
        return result;
    }

    internal static byte[] Shift(byte[] source, int pixels)
    {
        var result = new byte[source.Length];
        const int width = 24;
        const int height = 16;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + Math.Max(0, x - pixels)) * 4,
                result, (y * width + x) * 4, 4);
        return result;
    }

    internal static byte[] Quantize(byte[] source, int step)
    {
        var result = source.ToArray();
        for (var index = 0; index < result.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            result[index + channel] = (byte)(result[index + channel] / step * step);
        return result;
    }

    internal static byte[] ResizeNearest(
        byte[] source, int width, int height, int outputWidth, int outputHeight)
    {
        var result = new byte[outputWidth * outputHeight * 4];
        for (var y = 0; y < outputHeight; y++)
        for (var x = 0; x < outputWidth; x++)
            Array.Copy(source, ((y * height / outputHeight) * width + x * width / outputWidth) * 4,
                result, (y * outputWidth + x) * 4, 4);
        return result;
    }

    internal static byte[] Crop(byte[] source, int width, int cropWidth, int cropHeight)
    {
        var result = new byte[cropWidth * cropHeight * 4];
        for (var y = 0; y < cropHeight; y++)
        for (var x = 0; x < cropWidth; x++)
            Array.Copy(source, (y * width + x) * 4, result, (y * cropWidth + x) * 4, 4);
        return result;
    }

    internal static byte[] Mirror(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + width - 1 - x) * 4,
                result, (y * width + x) * 4, 4);
        return result;
    }

    internal static byte[] RotateClockwise(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + x) * 4,
                result, (x * height + height - 1 - y) * 4, 4);
        return result;
    }

    internal static byte[] Checkerboard(int width, int height)
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
}

internal sealed record NearQualityPair(NearQualityFrame Left, NearQualityFrame Right);

internal sealed record NearQualityFrame(int Width, int Height, byte[] Pixels);
