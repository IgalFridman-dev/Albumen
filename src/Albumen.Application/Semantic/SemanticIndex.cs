using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;

namespace Albumen.Application.Semantic;

/// <summary>
/// The current Scan's photo vectors. Independent of every non-semantic outcome: no state here
/// changes an Analysis Outcome, a group, Selection, the unresolved totals or an Action.
/// </summary>
public enum SemanticState
{
    /// <summary>Not prepared: no indexing for this Scan, or its vectors were cleared.</summary>
    Idle,
    Indexing,
    /// <summary>At least one vector; <see cref="SemanticSnapshot.IsPartial"/> discloses gaps.</summary>
    Ready,
    /// <summary>Indexing ended with no vector: no eligible photo, or none could be encoded.</summary>
    Empty,
    /// <summary>The model files are not installed.</summary>
    Unavailable,
    /// <summary>The model, the encoder or the vector store failed.</summary>
    Failed,
}

/// <param name="Total">Photos whose content the Scan proved; only they can get a vector.</param>
/// <param name="Processed">Photos the indexer finished, with or without a vector.</param>
/// <param name="Indexed">Photos whose vector is stored for this Scan under <paramref name="Profile"/>.</param>
/// <param name="Profile">The encoder profile fingerprint the vectors were made under.</param>
public sealed record SemanticSnapshot(
    SemanticState State,
    int Total,
    int Processed,
    int Indexed,
    string? Profile,
    string Reason)
{
    public static SemanticSnapshot Idle { get; } = new(SemanticState.Idle, 0, 0, 0, null, string.Empty);

    /// <summary>Some eligible photos have no vector (canceled, unreadable, changed or failed).</summary>
    public bool IsPartial => Indexed < Total;
}

/// <summary>One stored vector and the content it was made from.</summary>
public sealed record StoredEmbedding(long OccurrenceId, string Sha256, float[] Vector);

/// <summary>
/// Holds the vectors of one Scan under one encoder profile. Failures throw; the indexer turns
/// them into <see cref="SemanticState.Failed"/>.
/// </summary>
public interface IEmbeddingStore
{
    /// <summary>Removes every stored vector, of any Scan or profile.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);

    ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
        CancellationToken cancellationToken);

    /// <summary>Visits the vectors of this Scan made under exactly this profile, in occurrence order.</summary>
    ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
        CancellationToken cancellationToken);
}

/// <summary>Decodes a photo for the encoder after proving it still has the content the Scan hashed.</summary>
public interface IEncoderImageSource
{
    /// <returns>Null when the photo cannot be read or decoded, or its content changed.</returns>
    ValueTask<EncoderImage?> ReadAsync(ScanRunContext context, ScanFileOccurrence row, int maximumEdge,
        CancellationToken cancellationToken);
}

public interface ISemanticIndexer
{
    /// <summary>
    /// Embeds and stores the Scan's photos. Never throws for a model, photo or store failure, and
    /// returns the partial result when canceled.
    /// </summary>
    Task<SemanticSnapshot> IndexAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
        Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken);
}
