namespace PhotoSift.Infrastructure.Sqlite.Action;

internal static class ActionRevalidationSchema
{
    internal const string CreateTableSql = """
        CREATE TABLE action_revalidations (
          revalidation_id TEXT PRIMARY KEY NOT NULL,
          action_item_id TEXT NOT NULL,
          sequence INTEGER NOT NULL CHECK (sequence > 0),
          observed_utc TEXT NOT NULL,
          result_kind TEXT NOT NULL CHECK (result_kind IN ('confirmed_present', 'still_unknown')),
          reason TEXT NOT NULL CHECK (reason IN ('original_file_confirmed', 'original_path_missing',
            'original_path_changed', 'access_unavailable', 'location_unavailable', 'timed_out', 'failed')),
          observation_payload TEXT NOT NULL,
          observation_payload_sha256 TEXT NOT NULL,
          UNIQUE (action_item_id, sequence),
          FOREIGN KEY (action_item_id) REFERENCES action_items(action_item_id)
        );
        """;
}
