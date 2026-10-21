using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Albumen.Application.Semantic;

namespace Albumen.Infrastructure.Sqlite.Semantic;

internal sealed class SqliteEmbeddingStore(SqliteDatabaseOwner owner) : IEmbeddingStore
{
    public async ValueTask ClearAsync(CancellationToken cancellationToken) =>
        await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            await AlbumenSchema.ValidateAsync(connection, AlbumenSchema.Version, token, transaction).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM scan_embeddings;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(embeddings);
        if (embeddings.Any(embedding => embedding.Vector.Length != profile.Dimension))
            throw new ArgumentException("Every vector must have the profile's dimension.", nameof(embeddings));
        await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            await AlbumenSchema.ValidateAsync(connection, AlbumenSchema.Version, token, transaction).ConfigureAwait(false);
            foreach (var embedding in embeddings)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO scan_embeddings (scan_id, occurrence_id, profile, sha256, vector)
                    VALUES ($scan_id, $occurrence_id, $profile, $sha256, $vector);
                    """;
                command.Parameters.AddWithValue("$scan_id", GuidText(scanId));
                command.Parameters.AddWithValue("$occurrence_id", embedding.OccurrenceId);
                command.Parameters.AddWithValue("$profile", profile.Fingerprint);
                command.Parameters.AddWithValue("$sha256", embedding.Sha256);
                command.Parameters.AddWithValue("$vector", MemoryMarshal.AsBytes(embedding.Vector.AsSpan()).ToArray());
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(visit);
        await owner.RunReadAsync(async (connection, token) =>
        {
            await AlbumenSchema.ValidateAsync(connection, AlbumenSchema.Version, token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT occurrence_id, sha256, vector FROM scan_embeddings
                WHERE scan_id = $scan_id AND profile = $profile
                ORDER BY occurrence_id;
                """;
            command.Parameters.AddWithValue("$scan_id", GuidText(scanId));
            command.Parameters.AddWithValue("$profile", profile.Fingerprint);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var bytes = (byte[])reader.GetValue(2);
                if (bytes.Length != profile.Dimension * sizeof(float))
                    throw new InvalidDataException("A stored vector does not have the profile's dimension.");
                visit(new(reader.GetInt64(0), reader.GetString(1), MemoryMarshal.Cast<byte, float>(bytes).ToArray()));
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
}
