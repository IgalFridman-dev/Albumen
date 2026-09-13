namespace PhotoSift.Infrastructure.Sqlite.Action;

internal static class ActionJournalSchema
{
    internal const string CreateItemsSql = """
        CREATE TABLE action_items (
          action_item_id TEXT PRIMARY KEY NOT NULL,
          request_id TEXT NOT NULL,
          request_payload_sha256 TEXT NOT NULL,
          occurrence_id INTEGER NOT NULL,
          occurrence_revision INTEGER NOT NULL CHECK (occurrence_revision > 0),
          requested_utc TEXT NOT NULL,
          requested_payload TEXT NOT NULL,
          requested_payload_sha256 TEXT NOT NULL,
          UNIQUE (request_id, occurrence_id)
        );
        """;
    internal const string CreateTransitionsSql = """
        CREATE TABLE action_transitions (
          action_item_id TEXT NOT NULL,
          sequence INTEGER NOT NULL CHECK (sequence IN (2, 3)),
          transition_kind TEXT NOT NULL CHECK (
            (sequence = 2 AND transition_kind = 'executing') OR
            (sequence = 3 AND transition_kind IN
              ('recycled', 'not_removed', 'unknown_pending_revalidation'))),
          recorded_utc TEXT NOT NULL,
          transition_payload TEXT NOT NULL,
          transition_payload_sha256 TEXT NOT NULL,
          PRIMARY KEY (action_item_id, sequence),
          FOREIGN KEY (action_item_id) REFERENCES action_items(action_item_id)
        );
        """;
}
