namespace Albumen.Application.Scan;

public enum SignatureKind
{
    Near,
    Sharpness,
    Vector,
}

/// <summary>
/// A feature of one content: the SHA-256 and length the Exact stage proved, the kind of feature and
/// the revision of the code and decode that made it. No path, timestamp or physical key.
/// </summary>
public sealed record SignatureKey(string Sha256, long ByteLength, SignatureKind Kind, string Revision);

/// <summary>Value is null only for a sharpness "no score".</summary>
public sealed record StoredSignature(SignatureKey Key, byte[]? Value);

/// <summary>
/// Features kept across Scans, keyed by content. Failures throw; callers count them as rejects and
/// compute the feature as if nothing were stored.
/// </summary>
public interface ISignatureStore
{
    /// <summary>The stored values of these keys; a key that is absent is a miss.</summary>
    ValueTask<IReadOnlyDictionary<SignatureKey, byte[]?>> LookupAsync(
        IReadOnlyCollection<SignatureKey> keys, CancellationToken cancellationToken);

    /// <summary>Adds the signatures in one transaction; a key already stored keeps its value.</summary>
    ValueTask AddAsync(IReadOnlyList<StoredSignature> signatures, CancellationToken cancellationToken);
}
