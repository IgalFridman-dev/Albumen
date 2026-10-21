using System.Collections.Immutable;
using System.Text;
using Albumen.Application.Scan;

namespace Albumen.Application.Semantic;

public enum LikelyMatchesOutcome
{
    /// <summary>The prompt was empty after trimming: nothing is projected.</summary>
    Cleared,
    /// <summary>The prompt is longer than <see cref="LikelyMatchesQuery.MaximumPromptLength"/>; nothing was encoded.</summary>
    TooLong,
    Matches,
    /// <summary>No current photo reached <see cref="LikelyMatchesQuery.MinimumScore"/>.</summary>
    NoMatches,
    /// <summary>The Scan has no searchable index (see <see cref="ScanSnapshot.Semantic"/>).</summary>
    NotReady,
    Unavailable,
    Failed,
}

/// <summary>One photo of the Scan and the model's probability that the prompt fits it.</summary>
public sealed record LikelyMatch(long OccurrenceId, float Score);

public sealed record LikelyMatchesResult(
    LikelyMatchesOutcome Outcome,
    string Prompt,
    ImmutableArray<LikelyMatch> Matches,
    string Reason = "")
{
    internal static LikelyMatchesResult Of(LikelyMatchesOutcome outcome, string prompt, string reason = "") =>
        new(outcome, prompt, [], reason);
}

/// <summary>
/// Ranks the current Scan's photos against a short prompt: the prompt is encoded once and scored
/// against the vectors the Scan stored, so a repeated prompt never touches a photo. A vector is used
/// only while its SHA-256 is still the occurrence's content in the Scan. Read-only: it
/// changes no Analysis Outcome, group, Selection or Action. Review's Likely matches list uses it, and
/// so will the 1.2 Review filter (PM-019).
/// </summary>
public sealed class LikelyMatchesQuery(IVisionLanguageEncoder encoder, IEmbeddingStore store)
{
    /// <summary>Longest prompt accepted, in characters. Longer prompts are rejected, never cut.</summary>
    public const int MaximumPromptLength = 60;

    /// <summary>
    /// Lowest probability ever listed. The model's probability is small even for a photo that fits
    /// when the words are generic (owner's photos, 2026-10-21: 0.03% to 0.3% for "food", 5% to 54%
    /// for "jetski"), so this drops only photos the model finds unrelated to the words.
    /// </summary>
    public const float MinimumScore = 0.0001f;

    /// <summary>
    /// A photo is listed only while the model's odds that the words fit it are at least this
    /// fraction of the odds for the best photo in the Scan. The list stops where photos become far
    /// less likely than the best one: "jetski" lists the jetski photos, not every sea photo that
    /// scores a little. Measured on the owner's photos, the line fell about 0.008 in cosine from the
    /// nearest photo on either side for "jetski" and "food".
    /// </summary>
    public const double LeastOddsRelativeToBest = 1.0 / 50;

    /// <summary>
    /// SigLIP was trained on captions, so a bare word such as "jetski" is encoded as the caption
    /// "a photo of jetski". A prompt that already says "photo" is encoded as written.
    /// </summary>
    public const string PromptTemplate = "a photo of ";

    // Cosines equal to this many decimals are a tie, broken by occurrence id, so CPU rounding
    // differences between machines cannot reorder near-equal photos.
    private const int CosineDecimals = 5;

    private readonly IVisionLanguageEncoder encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
    private readonly IEmbeddingStore store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Trims and collapses whitespace; the result is what is encoded and shown back.</summary>
    public static string Normalize(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var words = prompt.Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }

    /// <summary>
    /// Ranks the photos of <paramref name="scan"/> by descending cosine, ties by occurrence id, and
    /// keeps those within <see cref="LeastOddsRelativeToBest"/> of the best photo and at or above
    /// <see cref="MinimumScore"/>. Model and store failures are returned, never thrown.
    /// </summary>
    public async Task<LikelyMatchesResult> RankAsync(ScanSnapshot scan, string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var normalized = Normalize(prompt);
        if (normalized.Length == 0) return LikelyMatchesResult.Of(LikelyMatchesOutcome.Cleared, normalized);
        if (normalized.Length > MaximumPromptLength)
            return LikelyMatchesResult.Of(LikelyMatchesOutcome.TooLong, normalized);
        var index = scan.Semantic;
        if (scan.Pin is not { } pin || index.State != SemanticState.Ready)
            return LikelyMatchesResult.Of(LikelyMatchesOutcome.NotReady, normalized);

        var loaded = encoder.LoadProfile();
        if (loaded.Failure is { } notLoaded) return Failure(normalized, notLoaded);
        var profile = loaded.Profile!;
        if (profile.Fingerprint != index.Profile)
            return LikelyMatchesResult.Of(LikelyMatchesOutcome.Failed, normalized,
                "The search model changed after this Scan. Scan again to search.");

        // The first prompt loads the text model, which takes about a second: keep it off the caller.
        var caption = normalized.Contains("photo", StringComparison.OrdinalIgnoreCase) ? normalized : PromptTemplate + normalized;
        var text = await Task.Run(() => encoder.EncodeText(profile, caption), cancellationToken).ConfigureAwait(false);
        if (text.Failure is { } failure) return Failure(normalized, failure);

        var content = scan.Occurrences
            .Where(row => row.Content is not null)
            .ToDictionary(row => row.Token.OccurrenceId, row => row.Content!.Sha256);
        var cosines = new List<(long OccurrenceId, float Cosine)>();
        try
        {
            // Visits one stored vector at a time and keeps only its cosine.
            await store.ReadAsync(pin.ScanId, profile, stored =>
            {
                if (content.TryGetValue(stored.OccurrenceId, out var sha256)
                    && string.Equals(sha256, stored.Sha256, StringComparison.Ordinal))
                    cosines.Add((stored.OccurrenceId, Dot(text.Vector.Span, stored.Vector)));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return LikelyMatchesResult.Of(LikelyMatchesOutcome.Failed, normalized,
                "The photo search data could not be read.");
        }

        // The odds are exp(scale * cosine + bias), so a fraction of the best odds is a fixed step
        // down in cosine from the best photo.
        var floor = Math.Max(CosineAt(profile, MinimumScore),
            cosines.Count == 0 ? 0 : cosines.Max(row => row.Cosine) + Math.Log(LeastOddsRelativeToBest) / profile.LogitScale);
        var ranked = cosines
            .Where(row => row.Cosine >= floor)
            .OrderByDescending(row => MathF.Round(row.Cosine, CosineDecimals))
            .ThenBy(row => row.OccurrenceId)
            .Select(row => new LikelyMatch(row.OccurrenceId, Probability(profile, row.Cosine)))
            .ToImmutableArray();
        return new(ranked.IsEmpty ? LikelyMatchesOutcome.NoMatches : LikelyMatchesOutcome.Matches, normalized, ranked);
    }

    private static LikelyMatchesResult Failure(string prompt, EncoderFailure failure) => LikelyMatchesResult.Of(
        failure.Kind == EncoderFailureKind.Unavailable ? LikelyMatchesOutcome.Unavailable : LikelyMatchesOutcome.Failed,
        prompt, failure.Reason);

    // The model's own reading of a cosine: sigmoid(scale * cosine + bias), as it was trained.
    private static float Probability(EncoderProfile profile, float cosine) =>
        (float)(1 / (1 + Math.Exp(-(profile.LogitScale * cosine + profile.LogitBias))));

    // The cosine at which the model's probability is exactly this.
    private static double CosineAt(EncoderProfile profile, double probability) =>
        (Math.Log(probability / (1 - probability)) - profile.LogitBias) / profile.LogitScale;

    // Both vectors are L2-normalized, so their dot product is the cosine.
    private static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length) throw new InvalidDataException("A stored vector does not match the prompt's dimension.");
        var sum = 0.0;
        for (var i = 0; i < left.Length; i++) sum += (double)left[i] * right[i];
        return (float)sum;
    }
}
