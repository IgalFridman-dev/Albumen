using Microsoft.Data.Sqlite;
using Albumen.Application.Scan;

namespace Albumen.Infrastructure.Sqlite.Signatures;

internal sealed class SqliteSignatureStore(SqliteDatabaseOwner owner) : ISignatureStore
{
    public async ValueTask<IReadOnlyDictionary<SignatureKey, byte[]?>> LookupAsync(
        IReadOnlyCollection<SignatureKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return new Dictionary<SignatureKey, byte[]?>();
        return await owner.RunReadAsync(async (connection, token) =>
        {
            await AlbumenSchema.ValidateAsync(connection, AlbumenSchema.Version, token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value FROM signatures
                WHERE sha256 = $sha256 AND byte_length = $byte_length AND kind = $kind AND revision = $revision;
                """;
            var found = new Dictionary<SignatureKey, byte[]?>();
            // One primary-key probe per key.
            foreach (var key in keys)
            {
                Bind(command, key);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    found[key] = reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
            }

            return (IReadOnlyDictionary<SignatureKey, byte[]?>)found;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddAsync(IReadOnlyList<StoredSignature> signatures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        if (signatures.Count == 0) return;
        await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            await AlbumenSchema.ValidateAsync(connection, AlbumenSchema.Version, token, transaction).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Two copies of one content in one Scan both compute it: the first stored value stays. Only
            // the key conflict is skipped (OR IGNORE would skip a failed CHECK too).
            command.CommandText = """
                INSERT INTO signatures (sha256, byte_length, kind, revision, value)
                VALUES ($sha256, $byte_length, $kind, $revision, $value)
                ON CONFLICT(sha256, byte_length, kind, revision) DO NOTHING;
                """;
            foreach (var signature in signatures)
            {
                Bind(command, signature.Key);
                command.Parameters.AddWithValue("$value", (object?)signature.Value ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, SignatureKey key)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$sha256", key.Sha256);
        command.Parameters.AddWithValue("$byte_length", key.ByteLength);
        command.Parameters.AddWithValue("$kind", key.Kind switch
        {
            SignatureKind.Near => "near",
            SignatureKind.Sharpness => "sharpness",
            SignatureKind.Vector => "vector",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Unknown signature kind."),
        });
        command.Parameters.AddWithValue("$revision", key.Revision);
    }
}
