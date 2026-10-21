namespace Albumen.Application.Scan;

/// <summary>
/// When the photo model makes two photos Maybe similar (E4-S6). The model never makes a Look-alike
/// (Near) pair: <see cref="NearDuplicateThresholds"/> alone decides those.
/// </summary>
public static class MaybeSimilarThresholds
{
    /// <summary>
    /// Provisional until the labelled corpus sets the threshold for precision first (decision D3);
    /// that result replaces <see cref="CosineMinimum"/> and this revision.
    /// </summary>
    public const string Revision = "provisional-2026-10-21";

    public const float CosineMinimum = 0.92f;

    /// <summary>
    /// Most photos in a set that a model match helped build. Sets the hash alone builds have no cap,
    /// as in 1.0.
    /// </summary>
    public const int MaximumSetSize = 30;

    public static bool IsMatch(float cosine) => cosine >= CosineMinimum;
}
