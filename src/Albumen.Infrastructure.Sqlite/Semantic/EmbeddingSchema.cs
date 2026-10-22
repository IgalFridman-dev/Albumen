namespace Albumen.Infrastructure.Sqlite.Semantic;

internal static class EmbeddingSchema
{
    // One row per photo of one Scan: the SigLIP 2 vector as little-endian float32, the encoder
    // profile fingerprint it was made under and the SHA-256 of the content it was made from.
    internal const string CreateTableSql = """
        CREATE TABLE scan_embeddings (
          scan_id TEXT NOT NULL,
          occurrence_id INTEGER NOT NULL CHECK(occurrence_id > 0),
          profile TEXT NOT NULL CHECK(length(profile) = 64 AND profile NOT GLOB '*[^0-9a-f]*'),
          sha256 TEXT NOT NULL CHECK(length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9A-F]*'),
          vector BLOB NOT NULL CHECK(length(vector) > 0 AND length(vector) % 4 = 0),
          PRIMARY KEY(scan_id, occurrence_id),
          FOREIGN KEY(scan_id, occurrence_id)
            REFERENCES scan_file_occurrences(scan_id, occurrence_id) ON DELETE RESTRICT
        );
        """;
}
