using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Sqlite.Review;

internal sealed class SqliteOverrideEvidenceStore : IOverrideEvidenceStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly SqliteDatabaseOwner owner;
    private readonly SqliteOverrideEvidenceStoreObserver? observer;

    internal SqliteOverrideEvidenceStore(SqliteDatabaseOwner owner,
        SqliteOverrideEvidenceStoreObserver? observer = null)
    {
        this.owner = owner;
        this.observer = observer;
    }

    internal static SqliteOverrideEvidenceStore CreateForTesting(string path,
        SqliteOverrideEvidenceStoreObserver? observer = null) => new(new(path), observer);

    public async ValueTask<OverridePersistResult> PersistAsync(
        DeliberateOverrideEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            return await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                var row = Canonicalize(evidence);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction).ConfigureAwait(false);
                var existing = await ReadRowAsync(connection, transaction, row.EvidenceId, token).ConfigureAwait(false);
                if (existing is not null)
                    return new OverridePersistResult(TryReadEvidence(existing, out var decoded) && Canonicalize(decoded) == row
                        ? OverridePersistOutcome.ExactRetry : OverridePersistOutcome.Conflict);
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
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (observer?.AfterInsert is { } after) await after(connection, transaction, token).ConfigureAwait(false);
                var inserted = await ReadRowAsync(connection, transaction, row.EvidenceId, token).ConfigureAwait(false);
                if (inserted is null || inserted != row || !TryReadEvidence(inserted, out _))
                    throw new InvalidDataException("SQLite did not retain the exact canonical override evidence.");
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new OverridePersistResult(OverridePersistOutcome.Persisted);
            }, cancellationToken, observer).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return new(OverridePersistOutcome.Unavailable);
        }
    }

    public async ValueTask<OverrideReadResult> ReadAsync(OverrideEvidenceId evidenceId, CancellationToken cancellationToken)
    {
        try
        {
            return await owner.RunReadAsync(async (connection, token) =>
            {
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token).ConfigureAwait(false);
                var row = await ReadRowAsync(connection, null, GuidText(evidenceId.Value), token).ConfigureAwait(false);
                if (row is null) return new OverrideReadResult(OverrideReadOutcome.Missing, null);
                return TryReadEvidence(row, out var evidence)
                    ? new OverrideReadResult(OverrideReadOutcome.Found, evidence)
                    : new OverrideReadResult(OverrideReadOutcome.Corrupt, null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return new(OverrideReadOutcome.Unavailable, null);
        }
    }

    public ValueTask DisposeAsync() => owner.DisposeAsync();

    internal static string CanonicalPayload(DeliberateOverrideEvidence evidence) =>
        JsonSerializer.Serialize(Canonicalize(evidence), JsonOptions);
    internal static string PayloadHash(DeliberateOverrideEvidence evidence) => Canonicalize(evidence).PayloadSha256;

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

    private static bool TryGuid(string text, out Guid value) =>
        Guid.TryParseExact(text, "D", out value)
        && GuidText(value).Equals(text, StringComparison.Ordinal);

    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

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
    System.Action<bool> LaneWaitStarted,
    Func<CancellationToken, ValueTask> LaneEntered,
    Func<SqliteConnection, CancellationToken, ValueTask>? ConnectionInitialized = null,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask>? AfterInsert = null);
