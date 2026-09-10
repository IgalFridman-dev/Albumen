using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Sqlite.Review;

public sealed class SqliteOverrideEvidenceStore : IOverrideEvidenceStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string databasePath;
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly object lifecycle = new();
    private readonly SqliteOverrideEvidenceStoreObserver? observer;
    private Task? disposalTask;
    private TaskCompletionSource? drainCompletion;
    private int admittedOperations;

    public SqliteOverrideEvidenceStore(string databasePath)
        : this(databasePath, null)
    {
    }

    internal SqliteOverrideEvidenceStore(
        string databasePath,
        SqliteOverrideEvidenceStoreObserver? observer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.observer = observer;
    }

    public async ValueTask<OverridePersistResult> PersistAsync(
        DeliberateOverrideEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        AdmitOperation();
        try
        {
            var row = Canonicalize(evidence);
            var wait = lane.WaitAsync(cancellationToken);
            observer?.LaneWaitStarted(wait.IsCompleted);
            await wait.ConfigureAwait(false);
            try
            {
                if (observer is not null)
                {
                    await observer.LaneEntered(cancellationToken).ConfigureAwait(false);
                }

                await using var connection = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);
                if (!await SchemaIsExactAsync(connection, cancellationToken, transaction).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The SQLite override evidence schema changed before writing.");
                }
                var existing = await ReadRowAsync(connection, transaction, row.EvidenceId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    return new(TryReadEvidence(existing, out var existingEvidence)
                        && Canonicalize(existingEvidence) == row
                            ? OverridePersistOutcome.ExactRetry
                            : OverridePersistOutcome.Conflict);
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO override_evidence (
                        evidence_id, affirmed_utc, review_session_id, scan_id,
                        scope_revision, rule_set_revision, analysis_revision,
                        group_id, group_revision, prior_selection_revision,
                        selected_occurrence_ids, affected_physical_revisions,
                        survivor_policy_version, consequence_text_version,
                        deliberately_affirmed, payload_sha256)
                    VALUES (
                        $evidence_id, $affirmed_utc, $review_session_id, $scan_id,
                        $scope_revision, $rule_set_revision, $analysis_revision,
                        $group_id, $group_revision, $prior_selection_revision,
                        $selected_occurrence_ids, $affected_physical_revisions,
                        $survivor_policy_version, $consequence_text_version,
                        $deliberately_affirmed, $payload_sha256);
                    """;
                Bind(command, row);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (observer?.AfterInsert is { } afterInsert)
                {
                    await afterInsert(connection, transaction, cancellationToken).ConfigureAwait(false);
                }

                var inserted = await ReadRowAsync(connection, transaction, row.EvidenceId, cancellationToken)
                    .ConfigureAwait(false);
                if (inserted is null || inserted != row || !TryReadEvidence(inserted, out _))
                {
                    throw new InvalidDataException("SQLite did not retain the exact canonical override evidence.");
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(OverridePersistOutcome.Persisted);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                return new(OverridePersistOutcome.Unavailable);
            }
            finally
            {
                lane.Release();
            }
        }
        finally
        {
            CompleteOperation();
        }
    }

    public async ValueTask<OverrideReadResult> ReadAsync(
        OverrideEvidenceId evidenceId,
        CancellationToken cancellationToken)
    {
        AdmitOperation();
        try
        {
            var wait = lane.WaitAsync(cancellationToken);
            observer?.LaneWaitStarted(wait.IsCompleted);
            await wait.ConfigureAwait(false);
            try
            {
                if (observer is not null)
                {
                    await observer.LaneEntered(cancellationToken).ConfigureAwait(false);
                }

                await using var connection = await OpenInitializedAsync(cancellationToken).ConfigureAwait(false);
                var row = await ReadRowAsync(connection, null, GuidText(evidenceId.Value), cancellationToken)
                    .ConfigureAwait(false);
                if (row is null)
                {
                    return new(OverrideReadOutcome.Missing, null);
                }

                return TryReadEvidence(row, out var evidence)
                    ? new(OverrideReadOutcome.Found, evidence)
                    : new(OverrideReadOutcome.Corrupt, null);
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                return new(OverrideReadOutcome.Unavailable, null);
            }
            finally
            {
                lane.Release();
            }
        }
        finally
        {
            CompleteOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task dispose;
        lock (lifecycle)
        {
            dispose = disposalTask ??= BeginDisposal();
        }

        return new(dispose);
    }

    private void AdmitOperation()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposalTask is not null, this);
            admittedOperations++;
        }
    }

    private void CompleteOperation()
    {
        TaskCompletionSource? drained = null;
        lock (lifecycle)
        {
            admittedOperations--;
            if (admittedOperations == 0)
            {
                drained = drainCompletion;
            }
        }

        drained?.TrySetResult();
    }

    private Task BeginDisposal()
    {
        Task drain;
        if (admittedOperations == 0)
        {
            drain = Task.CompletedTask;
        }
        else
        {
            drainCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = drainCompletion.Task;
        }

        return DisposeAfterDrainAsync(drain);
    }

    private async Task DisposeAfterDrainAsync(Task drain)
    {
        await drain.ConfigureAwait(false);
        lane.Dispose();
    }

    private async Task<SqliteConnection> OpenInitializedAsync(CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            if (!await IntegrityIsValidAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("SQLite integrity validation failed.");
            }

            var version = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken)
                .ConfigureAwait(false);
            if (version == 0)
            {
                await MigrateVersionZeroAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            else if (version != OverrideEvidenceSchema.Version)
            {
                throw new InvalidDataException("The SQLite schema version is unsupported.");
            }

            if (!await SchemaIsExactAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The SQLite override evidence schema is invalid.");
            }

            var journalMode = await ScalarStringAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken)
                .ConfigureAwait(false);
            if (!journalMode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SQLite WAL mode could not be enabled.");
            }

            if (observer?.ConnectionInitialized is { } initialized)
            {
                await initialized(connection, cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task MigrateVersionZeroAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "BEGIN EXCLUSIVE;", cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken)
                .ConfigureAwait(false);
            if (version != 0)
            {
                throw new InvalidDataException("The SQLite schema version changed during migration.");
            }

            if (!await DatabaseHasNoUserSchemaAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The version-zero SQLite database contains unsupported schema objects.");
            }

            await ExecuteAsync(connection, OverrideEvidenceSchema.CreateTableSql, cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection,
                $"PRAGMA user_version = {OverrideEvidenceSchema.Version};", cancellationToken).ConfigureAwait(false);

            if (await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false)
                    != OverrideEvidenceSchema.Version
                || !await SchemaIsExactAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The migrated SQLite override evidence schema is invalid.");
            }

            await ExecuteAsync(connection, "COMMIT;", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Preserve the original migration failure.
            }

            throw;
        }
    }

    private static async Task<bool> DatabaseHasNoUserSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var count = await ScalarLongAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE substr(name, 1, 7) <> 'sqlite_';", cancellationToken)
            .ConfigureAwait(false);
        return count == 0;
    }

    private static async Task<bool> IntegrityIsValidAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && reader.GetString(0).Equals("ok", StringComparison.OrdinalIgnoreCase)
            && !await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SchemaIsExactAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using (var objects = connection.CreateCommand())
        {
            objects.Transaction = transaction;
            objects.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY name;";
            await using var reader = await objects.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !reader.GetString(0).Equals("table", StringComparison.Ordinal)
                || !reader.GetString(1).Equals("override_evidence", StringComparison.Ordinal)
                || !reader.GetString(2).Equals("override_evidence", StringComparison.Ordinal)
                || reader.IsDBNull(3)
                || !NormalizeSql(reader.GetString(3)).Equals(NormalizeSql(OverrideEvidenceSchema.CreateTableSql),
                    StringComparison.OrdinalIgnoreCase)
                || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || !reader.GetString(0).Equals("index", StringComparison.Ordinal)
                || !reader.GetString(1).Equals("sqlite_autoindex_override_evidence_1", StringComparison.Ordinal)
                || !reader.GetString(2).Equals("override_evidence", StringComparison.Ordinal)
                || !reader.IsDBNull(3)
                || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        await using (var columns = connection.CreateCommand())
        {
            columns.Transaction = transaction;
            columns.CommandText = "PRAGMA table_info('override_evidence');";
            await using var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            foreach (var expected in OverrideEvidenceSchema.Columns)
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || !reader.GetString(1).Equals(expected.Name, StringComparison.Ordinal)
                    || !reader.GetString(2).Equals(expected.Type, StringComparison.OrdinalIgnoreCase)
                    || (reader.GetInt64(3) != 0) != expected.NotNull
                    || (reader.GetInt64(5) != 0) != expected.PrimaryKey)
                {
                    return false;
                }
            }

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<StoredRow?> ReadRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string evidenceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT evidence_id, affirmed_utc, review_session_id, scan_id,
                   scope_revision, rule_set_revision, analysis_revision,
                   group_id, group_revision, prior_selection_revision,
                   selected_occurrence_ids, affected_physical_revisions,
                   survivor_policy_version, consequence_text_version,
                   deliberately_affirmed, payload_sha256
            FROM override_evidence
            WHERE evidence_id = $evidence_id;
            """;
        command.Parameters.AddWithValue("$evidence_id", evidenceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10), reader.GetString(11),
            reader.GetString(12), reader.GetString(13), reader.GetInt64(14), reader.GetString(15));
    }

    private static StoredRow Canonicalize(DeliberateOverrideEvidence evidence)
    {
        var selected = evidence.SelectedOccurrenceIds.Distinct().Order().ToArray();
        var affected = evidence.AffectedPhysicalRevisions
            .Select(value => new AffectedPayload(
                value.PhysicalFileKey.VolumeSerial,
                value.PhysicalFileKey.FileId.High,
                value.PhysicalFileKey.FileId.Low,
                value.Content.ByteLength,
                value.Content.Sha256.ToLowerInvariant(),
                value.Allocation.AllocatedBytes,
                value.Allocation.LinkCount,
                value.Allocation.Stable))
            .OrderBy(value => value.VolumeSerial)
            .ThenBy(value => value.FileIdHigh)
            .ThenBy(value => value.FileIdLow)
            .ToArray();
        var pin = evidence.Pin;
        var scan = pin.Session.Scan;
        var row = new StoredRow(
            GuidText(evidence.EvidenceId.Value),
            evidence.AffirmedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            GuidText(pin.Session.ReviewSessionId),
            GuidText(scan.ScanId),
            scan.ScopeRevision,
            scan.RuleSetRevision,
            scan.AnalysisRevision,
            GuidText(pin.GroupId),
            pin.GroupRevision,
            pin.SelectionRevision,
            JsonSerializer.Serialize(selected, JsonOptions),
            JsonSerializer.Serialize(affected, JsonOptions),
            evidence.SurvivorPolicyVersion,
            evidence.ConsequenceTextVersion,
            evidence.DeliberatelyAffirmed ? 1 : 0,
            string.Empty);
        return row with { PayloadSha256 = Hash(row) };
    }

    private static bool TryReadEvidence(StoredRow row, out DeliberateOverrideEvidence evidence)
    {
        evidence = null!;
        try
        {
            if (!TryGuid(row.EvidenceId, out var evidenceId)
                || !TryGuid(row.ReviewSessionId, out var reviewSessionId)
                || !TryGuid(row.ScanId, out var scanId)
                || !TryGuid(row.GroupId, out var groupId)
                || row.GroupRevision <= 0
                || row.PriorSelectionRevision <= 0
                || row.DeliberatelyAffirmed != 1
                || !DateTime.TryParseExact(row.AffirmedUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var affirmed)
                || affirmed.Kind != DateTimeKind.Utc
                || !affirmed.ToString("O", CultureInfo.InvariantCulture).Equals(row.AffirmedUtc, StringComparison.Ordinal)
                || JsonSerializer.Deserialize<long[]>(row.SelectedOccurrenceIds, JsonOptions) is not { } selected
                || JsonSerializer.Deserialize<AffectedPayload?[]>(row.AffectedPhysicalRevisions, JsonOptions) is not { } affected
                || affected.Any(value => value is null))
            {
                return false;
            }

            var revisions = affected.Select(value =>
            {
                var payload = value!;
                var key = new PhysicalFileKey(payload.VolumeSerial, new(payload.FileIdHigh, payload.FileIdLow));
                return new AffectedPhysicalRevision(key,
                    new ContentRevision(key, payload.ByteLength, payload.Sha256),
                    new AllocationEvidence(payload.AllocatedBytes, payload.LinkCount, payload.AllocationStable));
            }).ToImmutableArray();
            evidence = new(
                new(evidenceId),
                new DateTimeOffset(affirmed),
                new(new(reviewSessionId, new(scanId, row.ScopeRevision, row.RuleSetRevision, row.AnalysisRevision)),
                    groupId, row.GroupRevision, row.PriorSelectionRevision),
                selected.ToImmutableArray(),
                revisions,
                row.SurvivorPolicyVersion,
                row.ConsequenceTextVersion,
                true);
            return Canonicalize(evidence) == row;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return false;
        }
    }

    private static void Bind(SqliteCommand command, StoredRow row)
    {
        command.Parameters.AddWithValue("$evidence_id", row.EvidenceId);
        command.Parameters.AddWithValue("$affirmed_utc", row.AffirmedUtc);
        command.Parameters.AddWithValue("$review_session_id", row.ReviewSessionId);
        command.Parameters.AddWithValue("$scan_id", row.ScanId);
        command.Parameters.AddWithValue("$scope_revision", row.ScopeRevision);
        command.Parameters.AddWithValue("$rule_set_revision", row.RuleSetRevision);
        command.Parameters.AddWithValue("$analysis_revision", row.AnalysisRevision);
        command.Parameters.AddWithValue("$group_id", row.GroupId);
        command.Parameters.AddWithValue("$group_revision", row.GroupRevision);
        command.Parameters.AddWithValue("$prior_selection_revision", row.PriorSelectionRevision);
        command.Parameters.AddWithValue("$selected_occurrence_ids", row.SelectedOccurrenceIds);
        command.Parameters.AddWithValue("$affected_physical_revisions", row.AffectedPhysicalRevisions);
        command.Parameters.AddWithValue("$survivor_policy_version", row.SurvivorPolicyVersion);
        command.Parameters.AddWithValue("$consequence_text_version", row.ConsequenceTextVersion);
        command.Parameters.AddWithValue("$deliberately_affirmed", row.DeliberatelyAffirmed);
        command.Parameters.AddWithValue("$payload_sha256", row.PayloadSha256);
    }

    private static string Hash(StoredRow row)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("evidenceId", row.EvidenceId);
            writer.WriteString("affirmedUtc", row.AffirmedUtc);
            writer.WriteString("reviewSessionId", row.ReviewSessionId);
            writer.WriteString("scanId", row.ScanId);
            writer.WriteNumber("scopeRevision", row.ScopeRevision);
            writer.WriteNumber("ruleSetRevision", row.RuleSetRevision);
            writer.WriteNumber("analysisRevision", row.AnalysisRevision);
            writer.WriteString("groupId", row.GroupId);
            writer.WriteNumber("groupRevision", row.GroupRevision);
            writer.WriteNumber("priorSelectionRevision", row.PriorSelectionRevision);
            writer.WritePropertyName("selectedOccurrenceIds");
            writer.WriteRawValue(row.SelectedOccurrenceIds);
            writer.WritePropertyName("affectedPhysicalRevisions");
            writer.WriteRawValue(row.AffectedPhysicalRevisions);
            writer.WriteString("survivorPolicyVersion", row.SurvivorPolicyVersion);
            writer.WriteString("consequenceTextVersion", row.ConsequenceTextVersion);
            writer.WriteBoolean("deliberatelyAffirmed", row.DeliberatelyAffirmed == 1);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, null, sql, cancellationToken).ConfigureAwait(false);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken) =>
        await ScalarLongAsync(connection, null, sql, cancellationToken).ConfigureAwait(false);

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGuid(string text, out Guid value) =>
        Guid.TryParseExact(text, "D", out value)
        && GuidText(value).Equals(text, StringComparison.Ordinal);

    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string NormalizeSql(string sql) => string.Join(' ',
        sql.Trim().TrimEnd(';').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsUnavailable(Exception exception) => exception is SqliteException
        or IOException
        or UnauthorizedAccessException
        or InvalidDataException;

    private sealed record StoredRow(
        string EvidenceId,
        string AffirmedUtc,
        string ReviewSessionId,
        string ScanId,
        long ScopeRevision,
        long RuleSetRevision,
        long AnalysisRevision,
        string GroupId,
        long GroupRevision,
        long PriorSelectionRevision,
        string SelectedOccurrenceIds,
        string AffectedPhysicalRevisions,
        string SurvivorPolicyVersion,
        string ConsequenceTextVersion,
        long DeliberatelyAffirmed,
        string PayloadSha256);

    private sealed record AffectedPayload(
        ulong VolumeSerial,
        ulong FileIdHigh,
        ulong FileIdLow,
        long ByteLength,
        string Sha256,
        long? AllocatedBytes,
        uint? LinkCount,
        bool AllocationStable);
}

internal sealed record SqliteOverrideEvidenceStoreObserver(
    Action<bool> LaneWaitStarted,
    Func<CancellationToken, ValueTask> LaneEntered,
    Func<SqliteConnection, CancellationToken, ValueTask>? ConnectionInitialized = null,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>? AfterInsert = null);
