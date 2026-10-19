namespace Albumen.Infrastructure.Sqlite.Review;

internal static class OverrideEvidenceSchema
{
    internal const int Version = 1;

    internal const string CreateTableSql = """
        CREATE TABLE override_evidence (
          evidence_id TEXT PRIMARY KEY NOT NULL,
          affirmed_utc TEXT NOT NULL,
          review_session_id TEXT NOT NULL,
          scan_id TEXT NOT NULL,
          scope_revision INTEGER NOT NULL,
          rule_set_revision INTEGER NOT NULL,
          analysis_revision INTEGER NOT NULL,
          group_id TEXT NOT NULL,
          group_revision INTEGER NOT NULL CHECK (group_revision > 0),
          prior_selection_revision INTEGER NOT NULL CHECK (prior_selection_revision > 0),
          selected_occurrence_ids TEXT NOT NULL,
          affected_physical_revisions TEXT NOT NULL,
          survivor_policy_version TEXT NOT NULL,
          consequence_text_version TEXT NOT NULL,
          deliberately_affirmed INTEGER NOT NULL CHECK (deliberately_affirmed = 1),
          payload_sha256 TEXT NOT NULL
        );
        """;

    internal static readonly Column[] Columns =
    [
        new("evidence_id", "TEXT", true, true),
        new("affirmed_utc", "TEXT", true, false),
        new("review_session_id", "TEXT", true, false),
        new("scan_id", "TEXT", true, false),
        new("scope_revision", "INTEGER", true, false),
        new("rule_set_revision", "INTEGER", true, false),
        new("analysis_revision", "INTEGER", true, false),
        new("group_id", "TEXT", true, false),
        new("group_revision", "INTEGER", true, false),
        new("prior_selection_revision", "INTEGER", true, false),
        new("selected_occurrence_ids", "TEXT", true, false),
        new("affected_physical_revisions", "TEXT", true, false),
        new("survivor_policy_version", "TEXT", true, false),
        new("consequence_text_version", "TEXT", true, false),
        new("deliberately_affirmed", "INTEGER", true, false),
        new("payload_sha256", "TEXT", true, false),
    ];

    internal readonly record struct Column(string Name, string Type, bool NotNull, bool PrimaryKey);
}
