namespace Albumen.Infrastructure.Sqlite.Signatures;

internal static class SignatureSchema
{
    // One feature of one content, kept across Scans: keyed by the SHA-256 and length the Exact stage
    // proved, the feature kind and its revision. No Scan, path, timestamp or physical key. NULL is a
    // sharpness "no score"; length(NULL) is NULL and passes a CHECK, so NULL is ruled out separately.
    internal const string CreateTableSql = """
        CREATE TABLE signatures (
          sha256 TEXT NOT NULL CHECK(length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9A-F]*'),
          byte_length INTEGER NOT NULL CHECK(byte_length >= 0),
          kind TEXT NOT NULL CHECK(kind IN ('near', 'sharpness', 'vector')),
          revision TEXT NOT NULL CHECK(length(revision) > 0),
          value BLOB,
          PRIMARY KEY(sha256, byte_length, kind, revision),
          CHECK(value IS NOT NULL OR kind = 'sharpness'),
          CHECK((kind = 'near' AND length(value) = 12)
            OR (kind = 'sharpness' AND (value IS NULL OR length(value) = 8))
            OR (kind = 'vector' AND length(value) > 0 AND length(value) % 4 = 0))
        );
        """;
}
