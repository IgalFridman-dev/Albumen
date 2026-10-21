using System.Collections.Immutable;
using System.Runtime.Intrinsics;
using Albumen.Application.Scan;

namespace Albumen.Application.Semantic;

/// <summary>Two current-Scan photos whose vectors are at or above the Maybe-similar threshold.</summary>
public sealed record EmbeddingMatch(long Left, long Right, float Cosine);

public interface IEmbeddingMatcher
{
    /// <summary>
    /// The pairs among the Scan's stored vectors that are Maybe similar, ordered by
    /// <see cref="EmbeddingMatch.Left"/> then <see cref="EmbeddingMatch.Right"/>, with
    /// <c>Left &lt; Right</c>. Only vectors made under <paramref name="profile"/> from the content the
    /// Scan hashed (<paramref name="contents"/>: occurrence id to SHA-256) take part.
    /// </summary>
    ValueTask<ImmutableArray<EmbeddingMatch>> MatchAsync(Guid scanId, string profile,
        IReadOnlyDictionary<long, string> contents, CancellationToken cancellationToken);
}

/// <summary>
/// Brute-force cosine over the Scan's stored vectors, on every core. No vector database: one Scan's
/// vectors fit in memory and are compared once.
/// </summary>
public sealed class StoredEmbeddingMatcher(IVisionLanguageEncoder encoder, IEmbeddingStore store) : IEmbeddingMatcher
{
    public async ValueTask<ImmutableArray<EmbeddingMatch>> MatchAsync(Guid scanId, string profile,
        IReadOnlyDictionary<long, string> contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        // Vectors are compared only within one profile: a different installed model matches nothing.
        if (encoder.LoadProfile().Profile is not { } loaded || loaded.Fingerprint != profile) return [];
        var vectors = new List<StoredEmbedding>();
        await store.ReadAsync(scanId, loaded, embedding =>
        {
            if (contents.TryGetValue(embedding.OccurrenceId, out var sha256)
                && string.Equals(sha256, embedding.Sha256, StringComparison.OrdinalIgnoreCase))
                vectors.Add(embedding);
        }, cancellationToken).ConfigureAwait(false);
        return Pairs(vectors, cancellationToken);
    }

    public static ImmutableArray<EmbeddingMatch> Pairs(IReadOnlyList<StoredEmbedding> vectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var ordered = vectors.OrderBy(vector => vector.OccurrenceId).ToArray();
        var found = new List<EmbeddingMatch>();
        Parallel.For(0, ordered.Length, new ParallelOptions { CancellationToken = cancellationToken },
            () => new List<EmbeddingMatch>(),
            (left, _, local) =>
            {
                for (var right = left + 1; right < ordered.Length; right++)
                {
                    var cosine = Cosine(ordered[left].Vector, ordered[right].Vector);
                    if (MaybeSimilarThresholds.IsMatch(cosine))
                        local.Add(new(ordered[left].OccurrenceId, ordered[right].OccurrenceId, cosine));
                }
                return local;
            },
            local =>
            {
                lock (found) found.AddRange(local);
            });
        return [.. found.OrderBy(match => match.Left).ThenBy(match => match.Right)];
    }

    // The vectors are L2-normalized, so their dot product is the cosine. A fixed width of eight lanes,
    // summed in a fixed order, gives the same value on every CPU; Vector<T> would change with it.
    internal static float Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Vectors must have the same dimension.");
        var sum = Vector256<float>.Zero;
        var index = 0;
        for (; index <= left.Length - Vector256<float>.Count; index += Vector256<float>.Count)
            sum += Vector256.Create(left.AsSpan(index)) * Vector256.Create(right.AsSpan(index));
        var total = 0f;
        for (var lane = 0; lane < Vector256<float>.Count; lane++) total += sum.GetElement(lane);
        for (; index < left.Length; index++) total += left[index] * right[index];
        return total;
    }
}
