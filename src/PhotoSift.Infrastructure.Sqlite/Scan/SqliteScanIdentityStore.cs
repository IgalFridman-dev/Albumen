using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Sqlite.Scan;

internal sealed class SqliteScanIdentityStore(SqliteDatabaseOwner owner) : IScanIdentityStore
{
    public async ValueTask<ScanIdentityPersistResult> PersistScanAsync(
        ScanIdentityRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return await owner.RunWriteAsync<ScanIdentityPersistResult>(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                var canonical = Canonicalize(record);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction)
                    .ConfigureAwait(false);
                var existing = await ReadScanRowAsync(connection, transaction, canonical.ScanId, token)
                    .ConfigureAwait(false);
                if (existing is not null)
                    return new(TryDecode(existing, out var decoded) && Canonicalize(decoded) == canonical
                        ? ScanIdentityPersistOutcome.ExactRetry
                        : ScanIdentityPersistOutcome.Conflict);

                await InsertScanAsync(connection, transaction, canonical, token).ConfigureAwait(false);
                var inserted = await ReadScanRowAsync(connection, transaction, canonical.ScanId, token)
                    .ConfigureAwait(false);
                if (inserted != canonical || !TryDecode(inserted, out _))
                    throw new InvalidDataException("SQLite did not retain the exact canonical Scan identity.");
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new(ScanIdentityPersistOutcome.Persisted);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return new(ScanIdentityPersistOutcome.Unavailable);
        }
    }

    public async ValueTask<ScanIdentityPersistResult> PersistOccurrenceAsync(
        ScanOccurrenceIdentityRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return await owner.RunWriteAsync<ScanIdentityPersistResult>(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                var canonical = Canonicalize(record);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction)
                    .ConfigureAwait(false);
                var parentRow = await ReadScanRowAsync(connection, transaction, canonical.ScanId, token).ConfigureAwait(false);
                if (parentRow is null)
                    return new(ScanIdentityPersistOutcome.MissingScan);
                if (!TryDecode(parentRow, out var parent) || parent.Pin != record.Token.Pin)
                    return new(ScanIdentityPersistOutcome.Conflict);

                var existing = await ReadOccurrenceRowAsync(
                    connection, transaction, canonical.ScanId, canonical.OccurrenceId, token).ConfigureAwait(false);
                if (existing is not null)
                    return new(TryDecode(existing, parent.Pin, out var decoded) && Canonicalize(decoded) == canonical
                        ? ScanIdentityPersistOutcome.ExactRetry
                        : ScanIdentityPersistOutcome.Conflict);

                await InsertOccurrenceAsync(connection, transaction, canonical, token).ConfigureAwait(false);
                var inserted = await ReadOccurrenceRowAsync(
                    connection, transaction, canonical.ScanId, canonical.OccurrenceId, token).ConfigureAwait(false);
                if (inserted != canonical || !TryDecode(inserted, parent.Pin, out _))
                    throw new InvalidDataException("SQLite did not retain the exact canonical occurrence identity.");
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new(ScanIdentityPersistOutcome.Persisted);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return new(ScanIdentityPersistOutcome.Unavailable);
        }
    }

    public async ValueTask<ScanIdentityReadResult> ReadScanAsync(
        Guid scanId, CancellationToken cancellationToken)
    {
        ValidateScanId(scanId);
        try
        {
            return await owner.RunReadAsync<ScanIdentityReadResult>(async (connection, token) =>
            {
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token).ConfigureAwait(false);
                StoredScanRow? row;
                try { row = await ReadScanRowAsync(connection, null, GuidText(scanId), token).ConfigureAwait(false); }
                catch (Exception exception) when (IsMalformedValue(exception))
                {
                    return new(ScanIdentityReadOutcome.Corrupt, null);
                }
                if (row is null) return new(ScanIdentityReadOutcome.Missing, null);
                return TryDecode(row, out var decoded)
                    ? new(ScanIdentityReadOutcome.Found, decoded)
                    : new(ScanIdentityReadOutcome.Corrupt, null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return new(ScanIdentityReadOutcome.Unavailable, null);
        }
    }

    public async ValueTask<ScanOccurrenceIdentityReadResult> ReadOccurrencesAsync(
        Guid scanId, CancellationToken cancellationToken)
    {
        ValidateScanId(scanId);
        try
        {
            return await owner.RunReadAsync<ScanOccurrenceIdentityReadResult>(async (connection, token) =>
            {
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token).ConfigureAwait(false);
                try
                {
                    var parentRow = await ReadScanRowAsync(connection, null, GuidText(scanId), token).ConfigureAwait(false);
                    if (parentRow is null)
                        return new(ScanIdentityReadOutcome.Missing, ImmutableArray<ScanOccurrenceIdentityRecord>.Empty);
                    if (!TryDecode(parentRow, out var parent))
                        return new(ScanIdentityReadOutcome.Corrupt,
                            ImmutableArray<ScanOccurrenceIdentityRecord>.Empty);
                    var rows = await ReadOccurrenceRowsAsync(connection, GuidText(scanId), token).ConfigureAwait(false);
                    var records = ImmutableArray.CreateBuilder<ScanOccurrenceIdentityRecord>(rows.Count);
                    foreach (var row in rows)
                    {
                        if (!TryDecode(row, parent.Pin, out var decoded))
                            return new(ScanIdentityReadOutcome.Corrupt,
                                ImmutableArray<ScanOccurrenceIdentityRecord>.Empty);
                        records.Add(decoded);
                    }
                    return new(ScanIdentityReadOutcome.Found, records.MoveToImmutable());
                }
                catch (Exception exception) when (IsMalformedValue(exception))
                {
                    return new(ScanIdentityReadOutcome.Corrupt,
                        ImmutableArray<ScanOccurrenceIdentityRecord>.Empty);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return new(ScanIdentityReadOutcome.Unavailable, ImmutableArray<ScanOccurrenceIdentityRecord>.Empty);
        }
    }

    private static async Task InsertScanAsync(SqliteConnection connection, SqliteTransaction transaction,
        StoredScanRow row, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scans (
                scan_id, scope_revision, rule_set_revision, analysis_revision,
                selected_path, resolved_path, recursion, root_volume_serial,
                root_file_id_high, root_file_id_low, created_utc)
            VALUES (
                $scan_id, $scope_revision, $rule_set_revision, $analysis_revision,
                $selected_path, $resolved_path, $recursion, $root_volume_serial,
                $root_file_id_high, $root_file_id_low, $created_utc);
            """;
        command.Parameters.AddWithValue("$scan_id", row.ScanId);
        command.Parameters.AddWithValue("$scope_revision", row.ScopeRevision);
        command.Parameters.AddWithValue("$rule_set_revision", row.RuleSetRevision);
        command.Parameters.AddWithValue("$analysis_revision", row.AnalysisRevision);
        command.Parameters.AddWithValue("$selected_path", row.SelectedPath);
        command.Parameters.AddWithValue("$resolved_path", row.ResolvedPath);
        command.Parameters.AddWithValue("$recursion", row.Recursion);
        command.Parameters.AddWithValue("$root_volume_serial", row.RootVolumeSerial);
        command.Parameters.AddWithValue("$root_file_id_high", row.RootFileIdHigh);
        command.Parameters.AddWithValue("$root_file_id_low", row.RootFileIdLow);
        command.Parameters.AddWithValue("$created_utc", row.CreatedUtc);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertOccurrenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        StoredOccurrenceRow row, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_file_occurrences (
                scan_id, occurrence_id, occurrence_revision, attempt,
                discovered_path, resolved_path, volume_serial, file_id_high, file_id_low)
            VALUES (
                $scan_id, $occurrence_id, $occurrence_revision, $attempt,
                $discovered_path, $resolved_path, $volume_serial, $file_id_high, $file_id_low);
            """;
        command.Parameters.AddWithValue("$scan_id", row.ScanId);
        command.Parameters.AddWithValue("$occurrence_id", row.OccurrenceId);
        command.Parameters.AddWithValue("$occurrence_revision", row.OccurrenceRevision);
        command.Parameters.AddWithValue("$attempt", row.Attempt);
        command.Parameters.AddWithValue("$discovered_path", row.DiscoveredPath);
        command.Parameters.AddWithValue("$resolved_path", row.ResolvedPath);
        command.Parameters.AddWithValue("$volume_serial", (object?)row.VolumeSerial ?? DBNull.Value);
        command.Parameters.AddWithValue("$file_id_high", (object?)row.FileIdHigh ?? DBNull.Value);
        command.Parameters.AddWithValue("$file_id_low", (object?)row.FileIdLow ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<StoredScanRow?> ReadScanRowAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string scanId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, scope_revision, rule_set_revision, analysis_revision,
                   selected_path, resolved_path, recursion, root_volume_serial,
                   root_file_id_high, root_file_id_low, created_utc
            FROM scans WHERE scan_id = $scan_id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var row = new StoredScanRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10));
        if (await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("Multiple persisted Scan IDs differ only by canonical text form.");
        return row;
    }

    private static async Task<StoredOccurrenceRow?> ReadOccurrenceRowAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string scanId, long occurrenceId, CancellationToken token)
    {
        await using var command = OccurrenceCommand(connection, transaction, """
            WHERE scan_id = $scan_id AND occurrence_id = $occurrence_id
            ORDER BY occurrence_id;
            """);
        command.Parameters.AddWithValue("$scan_id", scanId);
        command.Parameters.AddWithValue("$occurrence_id", occurrenceId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadOccurrenceRow(reader) : null;
    }

    private static async Task<List<StoredOccurrenceRow>> ReadOccurrenceRowsAsync(
        SqliteConnection connection, string scanId, CancellationToken token)
    {
        await using var command = OccurrenceCommand(connection, null, """
            WHERE scan_id = $scan_id ORDER BY occurrence_id;
            """);
        command.Parameters.AddWithValue("$scan_id", scanId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<StoredOccurrenceRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(ReadOccurrenceRow(reader));
        return rows;
    }

    private static SqliteCommand OccurrenceCommand(SqliteConnection connection,
        SqliteTransaction? transaction, string suffix)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, occurrence_id, occurrence_revision, attempt,
                   discovered_path, resolved_path, volume_serial, file_id_high, file_id_low
            FROM scan_file_occurrences
            """ + " " + suffix;
        return command;
    }

    private static StoredOccurrenceRow ReadOccurrenceRow(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
        reader.GetString(4), reader.GetString(5), NullableString(reader, 6),
        NullableString(reader, 7), NullableString(reader, 8));

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static StoredScanRow Canonicalize(ScanIdentityRecord record) => new(
        GuidText(record.Pin.ScanId), record.Pin.ScopeRevision, record.Pin.RuleSetRevision,
        record.Pin.AnalysisRevision, record.Selection.SelectedPath, record.ResolvedPath,
        (long)record.Selection.Recursion, Hex(record.RootIdentity.VolumeSerialNumber),
        Hex(record.RootIdentity.FileIdHigh), Hex(record.RootIdentity.FileIdLow), Utc(record.CreatedUtc));

    private static StoredOccurrenceRow Canonicalize(ScanOccurrenceIdentityRecord record)
    {
        var physical = record.PhysicalFileKey;
        return new(GuidText(record.Token.Pin.ScanId), record.Token.OccurrenceId,
            record.Token.OccurrenceRevision, record.Token.Attempt, record.DiscoveredPath,
            record.ResolvedPath, physical is { } key ? Hex(key.VolumeSerial) : null,
            physical is { } high ? Hex(high.FileId.High) : null,
            physical is { } low ? Hex(low.FileId.Low) : null);
    }

    private static bool TryDecode(StoredScanRow? row, out ScanIdentityRecord record)
    {
        record = null!;
        if (row is null || !TryGuid(row.ScanId, out var scanId)
            || row.ScopeRevision <= 0 || row.RuleSetRevision <= 0 || row.AnalysisRevision <= 0
            || row.Recursion is < 0 or > 1
            || !TryHex(row.RootVolumeSerial, out var volume)
            || !TryHex(row.RootFileIdHigh, out var high)
            || !TryHex(row.RootFileIdLow, out var low)
            || !TryUtc(row.CreatedUtc, out var created)) return false;
        try
        {
            record = new(new(scanId, row.ScopeRevision, row.RuleSetRevision, row.AnalysisRevision),
                new(row.SelectedPath, (ScopeRecursion)row.Recursion), row.ResolvedPath,
                new(volume, high, low), created);
            return Canonicalize(record) == row;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool TryDecode(StoredOccurrenceRow? row, ScanPin parentPin,
        out ScanOccurrenceIdentityRecord record)
    {
        record = null!;
        if (row is null || !TryGuid(row.ScanId, out var scanId)
            || row.OccurrenceId <= 0 || row.OccurrenceRevision <= 0 || row.Attempt <= 0) return false;
        var allNull = row.VolumeSerial is null && row.FileIdHigh is null && row.FileIdLow is null;
        var allPresent = row.VolumeSerial is not null && row.FileIdHigh is not null && row.FileIdLow is not null;
        if (!allNull && !allPresent) return false;
        PhysicalFileKey? physical = null;
        if (allPresent)
        {
            if (!TryHex(row.VolumeSerial!, out var volume)
                || !TryHex(row.FileIdHigh!, out var high)
                || !TryHex(row.FileIdLow!, out var low)) return false;
            physical = new(volume, new(high, low));
        }
        try
        {
            if (parentPin.ScanId != scanId) return false;
            record = new(new(parentPin, row.OccurrenceId, row.OccurrenceRevision, row.Attempt),
                row.DiscoveredPath, row.ResolvedPath, physical);
            return CanonicalOccurrenceFields(record, row);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool CanonicalOccurrenceFields(ScanOccurrenceIdentityRecord record, StoredOccurrenceRow row) =>
        GuidText(record.Token.Pin.ScanId) == row.ScanId
        && record.Token.OccurrenceId == row.OccurrenceId
        && record.Token.OccurrenceRevision == row.OccurrenceRevision
        && record.Token.Attempt == row.Attempt
        && record.DiscoveredPath == row.DiscoveredPath
        && record.ResolvedPath == row.ResolvedPath
        && (record.PhysicalFileKey is { } key
            ? Hex(key.VolumeSerial) == row.VolumeSerial && Hex(key.FileId.High) == row.FileIdHigh
                && Hex(key.FileId.Low) == row.FileIdLow
            : row.VolumeSerial is null && row.FileIdHigh is null && row.FileIdLow is null);

    private static void ValidateScanId(Guid scanId)
    {
        if (scanId == Guid.Empty) throw new ArgumentException("Scan ID must be nonempty.", nameof(scanId));
    }

    private static bool IsMalformedValue(Exception exception) =>
        exception is InvalidCastException or FormatException or OverflowException or ArgumentException;
    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
    private static bool TryGuid(string text, out Guid value) => Guid.TryParseExact(text, "D", out value)
        && GuidText(value).Equals(text, StringComparison.Ordinal);
    private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
    private static bool TryHex(string text, out ulong value) => ulong.TryParse(
        text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
        && Hex(value).Equals(text, StringComparison.Ordinal);
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static bool TryUtc(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out value)
        && value.Offset == TimeSpan.Zero && Utc(value).Equals(text, StringComparison.Ordinal);

    private sealed record StoredScanRow(string ScanId, long ScopeRevision, long RuleSetRevision,
        long AnalysisRevision, string SelectedPath, string ResolvedPath, long Recursion,
        string RootVolumeSerial, string RootFileIdHigh, string RootFileIdLow, string CreatedUtc);

    private sealed record StoredOccurrenceRow(string ScanId, long OccurrenceId, long OccurrenceRevision,
        long Attempt, string DiscoveredPath, string ResolvedPath, string? VolumeSerial,
        string? FileIdHigh, string? FileIdLow);
}
