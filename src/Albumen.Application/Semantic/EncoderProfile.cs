using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Albumen.Application.Semantic;

/// <summary>
/// The complete inference contract a vector was made under. Vectors are compared only when their
/// fingerprints are equal; any field change gives a new fingerprint.
/// </summary>
/// <param name="PipelineRevision">
/// The adapter's fixed choices: resize and pixel scaling, channel order, prompt lower-casing,
/// truncation and padding, ONNX input and output names, and L2 normalization.
/// </param>
public sealed record EncoderProfile(
    string Model,
    string VisionSha256,
    string TextSha256,
    string TokenizerSha256,
    int DecodeEdge,
    int ImageSize,
    int MaxTokens,
    int PadId,
    int Dimension,
    int PipelineRevision)
{
    public const int SchemaVersion = 1;

    public string CanonicalForm => string.Join(
        '\n',
        Field("schema", SchemaVersion),
        "model=" + Model,
        "vision.sha256=" + VisionSha256,
        "text.sha256=" + TextSha256,
        "tokenizer.sha256=" + TokenizerSha256,
        Field("decode.edge", DecodeEdge),
        Field("image.size", ImageSize),
        Field("tokens.max", MaxTokens),
        Field("tokens.pad", PadId),
        Field("dimension", Dimension),
        Field("pipeline", PipelineRevision));

    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm)));

    private static string Field(string name, int value) =>
        name + "=" + value.ToString(CultureInfo.InvariantCulture);
}
