using System.Text.Json;
using System.Text.Json.Serialization;

namespace Albumen.Infrastructure.Onnx;

internal sealed record ModelFile(string Path, long Length, string Sha256);

internal sealed record ModelFiles(ModelFile Vision, ModelFile Text, ModelFile Tokenizer);

/// <summary><c>models/manifest.json</c>: the approved model files and the facts of their export.</summary>
internal sealed record ModelManifest(
    int SchemaVersion,
    string Model,
    string License,
    int ImageSize,
    int MaxTokens,
    int PadId,
    int Dimension,
    double LogitScale,
    double LogitBias,
    ModelFiles Files)
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public IEnumerable<ModelFile> All => [Files.Vision, Files.Text, Files.Tokenizer];

    /// <exception cref="JsonException">The manifest is malformed or incomplete.</exception>
    public static ModelManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<ModelManifest>(json, Options)
            ?? throw new JsonException("The model manifest is empty.");
        if (manifest.SchemaVersion != 1)
        {
            throw new JsonException("The model manifest schema version is not 1.");
        }

        if (manifest.ImageSize <= 0 || manifest.MaxTokens < 2 || manifest.PadId < 0 || manifest.Dimension <= 0)
        {
            throw new JsonException("The model manifest has an invalid size, token or dimension value.");
        }

        if (!double.IsFinite(manifest.LogitScale) || manifest.LogitScale <= 0 || !double.IsFinite(manifest.LogitBias))
        {
            throw new JsonException("The model manifest has an invalid logit scale or bias.");
        }

        foreach (var file in manifest.All)
        {
            if (file.Path != System.IO.Path.GetFileName(file.Path) || file.Path.Length == 0 || file.Length <= 0
                || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigitUpper))
            {
                throw new JsonException($"The model manifest entry '{file.Path}' is invalid.");
            }
        }

        return manifest;
    }
}
