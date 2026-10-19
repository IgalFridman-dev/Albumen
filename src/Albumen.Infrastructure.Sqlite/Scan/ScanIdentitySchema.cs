namespace Albumen.Infrastructure.Sqlite.Scan;

internal static class ScanIdentitySchema
{
    internal const string CreateScansSql = """
        CREATE TABLE scans (
          scan_id TEXT PRIMARY KEY NOT NULL,
          scope_revision INTEGER NOT NULL CHECK(scope_revision > 0),
          rule_set_revision INTEGER NOT NULL CHECK(rule_set_revision > 0),
          analysis_revision INTEGER NOT NULL CHECK(analysis_revision > 0),
          selected_path TEXT NOT NULL CHECK(length(selected_path) > 0),
          resolved_path TEXT NOT NULL CHECK(length(resolved_path) > 0),
          recursion INTEGER NOT NULL CHECK(recursion IN (0, 1)),
          root_volume_serial TEXT NOT NULL CHECK(length(root_volume_serial) = 16),
          root_file_id_high TEXT NOT NULL CHECK(length(root_file_id_high) = 16),
          root_file_id_low TEXT NOT NULL CHECK(length(root_file_id_low) = 16),
          created_utc TEXT NOT NULL
        );
        """;

    internal const string CreateOccurrencesSql = """
        CREATE TABLE scan_file_occurrences (
          scan_id TEXT NOT NULL,
          occurrence_id INTEGER NOT NULL CHECK(occurrence_id > 0),
          occurrence_revision INTEGER NOT NULL CHECK(occurrence_revision > 0),
          attempt INTEGER NOT NULL CHECK(attempt > 0),
          discovered_path TEXT NOT NULL CHECK(length(discovered_path) > 0),
          resolved_path TEXT NOT NULL CHECK(length(resolved_path) > 0),
          volume_serial TEXT NULL CHECK(volume_serial IS NULL OR length(volume_serial) = 16),
          file_id_high TEXT NULL CHECK(file_id_high IS NULL OR length(file_id_high) = 16),
          file_id_low TEXT NULL CHECK(file_id_low IS NULL OR length(file_id_low) = 16),
          PRIMARY KEY(scan_id, occurrence_id),
          FOREIGN KEY(scan_id) REFERENCES scans(scan_id) ON DELETE RESTRICT,
          CHECK((volume_serial IS NULL AND file_id_high IS NULL AND file_id_low IS NULL)
             OR (volume_serial IS NOT NULL AND file_id_high IS NOT NULL AND file_id_low IS NOT NULL))
        );
        """;
}
