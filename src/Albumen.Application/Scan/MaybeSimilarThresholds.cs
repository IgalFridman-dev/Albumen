namespace Albumen.Application.Scan;

/// <summary>
/// When the photo model makes two photos Maybe similar (E4-S6). The model never makes a Look-alike
/// (Near) pair: <see cref="NearDuplicateThresholds"/> alone decides those.
/// </summary>
public static class MaybeSimilarThresholds
{
    /// <summary>The labelled corpus that set <see cref="CosineMinimum"/> (E3-S6, decision D3).</summary>
    public const string Revision = "corpus-41317957f7a6";

    /// <summary>
    /// Set on the owner's labelled corpus, precision first (E3-S6, corpus 41317957f7a6): from 0.88 to
    /// 0.95 about 3 in 4 calls are right, the wrong ones being real look-alikes (one room on two
    /// days). 0.89 keeps that (74.8%, was 75.2% at 0.92) and finds 75% of same-set pairs, not 62%.
    /// </summary>
    public const float CosineMinimum = 0.89f;

    /// <summary>
    /// Most photos in a set that a model match helped build. Sets the hash alone builds have no cap,
    /// as in 1.0.
    /// </summary>
    public const int MaximumSetSize = 30;

    public static bool IsMatch(float cosine) => cosine >= CosineMinimum;
}
