namespace Albumen.Application.Semantic;

/// <summary>EXIF-oriented BGRA32 pixels, longest edge at most <see cref="EncoderProfile.DecodeEdge"/>.</summary>
public sealed record EncoderImage(ReadOnlyMemory<byte> Bgra, int Width, int Height);

/// <summary>Unavailable: the model files are not installed. Failed: they are present but unusable.</summary>
public enum EncoderFailureKind
{
    Unavailable,
    Failed,
}

public sealed record EncoderFailure(EncoderFailureKind Kind, string Reason);

public sealed record EncoderProfileResult(EncoderProfile? Profile, EncoderFailure? Failure);

/// <summary>An L2-normalized vector, or why there is none.</summary>
public sealed record EmbeddingResult(ReadOnlyMemory<float> Vector, EncoderFailure? Failure);

/// <summary>
/// Turns photos and prompts into vectors in one shared space. Failures are returned, never thrown,
/// and affect only semantic features.
/// </summary>
public interface IVisionLanguageEncoder
{
    /// <summary>The installed profile, checked for presence only; checksums are verified before first use.</summary>
    EncoderProfileResult LoadProfile();

    EmbeddingResult EncodeImage(EncoderProfile profile, EncoderImage image);

    EmbeddingResult EncodeText(EncoderProfile profile, string prompt);
}
