namespace PhotoSift.Infrastructure.Sqlite.Scan;

internal static class ScanCheckpointSchema
{
    private const string CanonicalTransitionIdCheck = """
        length(transition_id) = 36
            AND transition_id = lower(transition_id)
            AND substr(transition_id, 9, 1) = '-'
            AND substr(transition_id, 14, 1) = '-'
            AND substr(transition_id, 19, 1) = '-'
            AND substr(transition_id, 24, 1) = '-'
            AND length(replace(transition_id, '-', '')) = 32
            AND transition_id NOT GLOB '*[^0-9a-f-]*'
        """;

    internal static readonly string CreateLifecycleSql = $$"""
        CREATE TABLE scan_lifecycle (
          scan_id TEXT PRIMARY KEY NOT NULL,
          revision INTEGER NOT NULL CHECK(revision > 0),
          transition_id TEXT NOT NULL CHECK(
            {{CanonicalTransitionIdCheck}}),
          state INTEGER NOT NULL CHECK(state IN (0, 1, 2, 3, 4, 5, 6, 7)),
          FOREIGN KEY(scan_id) REFERENCES scans(scan_id) ON DELETE RESTRICT
        );
        """;

    internal static readonly string CreateStagesSql = $$"""
        CREATE TABLE scan_stage_checkpoints (
          scan_id TEXT NOT NULL,
          occurrence_id INTEGER NOT NULL CHECK(occurrence_id > 0),
          stage INTEGER NOT NULL CHECK(stage IN (0, 1)),
          revision INTEGER NOT NULL CHECK(revision > 0),
          transition_id TEXT NOT NULL CHECK(
            {{CanonicalTransitionIdCheck}}),
          state INTEGER NOT NULL CHECK(state IN (0, 1, 2, 3, 4, 5, 6, 7)),
          reason INTEGER NOT NULL CHECK(reason IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14)),
          prerequisite_stage INTEGER NULL CHECK(prerequisite_stage IS NULL OR prerequisite_stage IN (0, 1)),
          prerequisite_state INTEGER NULL CHECK(prerequisite_state IS NULL OR prerequisite_state IN (0, 1, 2, 3, 4, 5, 6, 7)),
          prerequisite_reason INTEGER NULL CHECK(prerequisite_reason IS NULL OR prerequisite_reason IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14)),
          PRIMARY KEY(scan_id, occurrence_id, stage),
          FOREIGN KEY(scan_id, occurrence_id)
            REFERENCES scan_file_occurrences(scan_id, occurrence_id) ON DELETE RESTRICT,
          CHECK((stage = 0 AND prerequisite_stage IS NULL AND prerequisite_state IS NULL AND prerequisite_reason IS NULL)
             OR (stage = 1 AND prerequisite_stage = 0 AND prerequisite_state IS NOT NULL AND prerequisite_reason IS NOT NULL))
        );
        """;

    internal const string CreateAccountingSql = """
        CREATE TABLE scan_accounting (
          scan_id TEXT PRIMARY KEY NOT NULL,
          discovered INTEGER NOT NULL CHECK(discovered >= 0),
          analyzed INTEGER NOT NULL CHECK(analyzed >= 0),
          unsupported INTEGER NOT NULL CHECK(unsupported >= 0),
          failed INTEGER NOT NULL CHECK(failed >= 0),
          canceled INTEGER NOT NULL CHECK(canceled >= 0),
          outstanding INTEGER NOT NULL CHECK(outstanding >= 0),
          FOREIGN KEY(scan_id) REFERENCES scan_lifecycle(scan_id) ON DELETE RESTRICT,
          CHECK(discovered = analyzed + unsupported + failed + canceled + outstanding)
        );
        """;
}
