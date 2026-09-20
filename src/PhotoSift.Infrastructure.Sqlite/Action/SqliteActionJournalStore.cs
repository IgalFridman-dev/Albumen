using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Action;
using PhotoSift.Infrastructure.Sqlite.Review;

namespace PhotoSift.Infrastructure.Sqlite.Action;

internal sealed class SqliteActionJournalStore(SqliteDatabaseOwner owner) : IActionJournalStore
{
    public ValueTask<ActionJournalWriteResult> PersistRequestedAsync(ActionRequestedFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return WriteAsync(async (connection, transaction, token) =>
        {
            if (!ValidRequested(fact)) return ActionJournalWriteOutcome.Conflict;
            var payload = RequestedPayload.Create(fact);
            var proposed = ItemRow.Create(payload);
            var rows = await ReadItemsAsync(connection, transaction, proposed.ActionItemId, proposed.RequestId, token).ConfigureAwait(false);
            ItemRow? existing = null;
            foreach (var row in rows)
            {
                var decoded = DecodeItem(row);
                if (row.RequestId == proposed.RequestId && (row.RequestPayloadSha256 != proposed.RequestPayloadSha256
                    || decoded.RequestIdentityPayload != payload.RequestIdentityPayload)) return ActionJournalWriteOutcome.Conflict;
                if (row.ActionItemId == proposed.ActionItemId)
                    existing = row;
                else if (row.RequestId == proposed.RequestId && row.OccurrenceId == proposed.OccurrenceId)
                    return ActionJournalWriteOutcome.Conflict;
            }
            if (existing is not null)
                return existing == proposed ? ActionJournalWriteOutcome.ExactRetry : ActionJournalWriteOutcome.Conflict;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO action_items (action_item_id, request_id, request_payload_sha256,
                  occurrence_id, occurrence_revision, requested_utc, requested_payload, requested_payload_sha256)
                VALUES ($id, $request, $requestHash, $occurrence, $revision, $utc, $payload, $hash);
                """;
            command.Parameters.AddWithValue("$id", proposed.ActionItemId);
            command.Parameters.AddWithValue("$request", proposed.RequestId);
            command.Parameters.AddWithValue("$requestHash", proposed.RequestPayloadSha256);
            command.Parameters.AddWithValue("$occurrence", proposed.OccurrenceId);
            command.Parameters.AddWithValue("$revision", proposed.OccurrenceRevision);
            command.Parameters.AddWithValue("$utc", proposed.RequestedUtc);
            command.Parameters.AddWithValue("$payload", proposed.Payload);
            command.Parameters.AddWithValue("$hash", proposed.PayloadSha256);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await owner.AfterActionInsertAsync(connection, transaction, token).ConfigureAwait(false);
            var inserted = await ReadItemsAsync(connection, transaction, proposed.ActionItemId, null, token).ConfigureAwait(false);
            if (inserted.Count != 1 || inserted[0] != proposed) throw new InvalidDataException("Requested readback mismatch.");
            _ = DecodeItem(inserted[0]);
            return ActionJournalWriteOutcome.Persisted;
        }, cancellationToken);
    }

    public ValueTask<ActionJournalWriteResult> PersistExecutingAsync(ActionExecutingFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return TransitionAsync(fact.ActionItemId, 2, "executing", fact.ExecutingUtc, CanonicalJson.Encode(fact), cancellationToken);
    }
    public ValueTask<ActionJournalWriteResult> PersistTerminalAsync(ActionTerminalFact fact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!Enum.IsDefined(fact.Kind) || !Enum.IsDefined(fact.Reason) || fact.Evidence is null)
            return ValueTask.FromResult(new ActionJournalWriteResult(ActionJournalWriteOutcome.Conflict));
        return TransitionAsync(fact.ActionItemId, 3, KindText(fact.Kind), fact.RecordedUtc, CanonicalJson.Encode(fact), cancellationToken);
    }

    private ValueTask<ActionJournalWriteResult> TransitionAsync(Guid id, int sequence, string kind,
        DateTimeOffset utc, string payload, CancellationToken cancellationToken) =>
        WriteAsync(async (connection, transaction, token) =>
        {
            var proposed = new TransitionRow(GuidText(id), sequence, kind, Utc(utc), payload, Hash(payload));
            var items = await ReadItemsAsync(connection, transaction, proposed.ActionItemId, null, token).ConfigureAwait(false);
            if (items.Count != 1) return ActionJournalWriteOutcome.InvalidPredecessor;
            var target = DecodeItem(items[0]);
            var siblings = await ReadItemsAsync(connection, transaction, proposed.ActionItemId, items[0].RequestId, token).ConfigureAwait(false);
            foreach (var sibling in siblings)
            {
                var decoded = DecodeItem(sibling);
                if (sibling.RequestPayloadSha256 != items[0].RequestPayloadSha256
                    || decoded.RequestIdentityPayload != target.RequestIdentityPayload)
                    return ActionJournalWriteOutcome.Conflict;
            }
            var transitions = await ReadTransitionsAsync(connection, transaction, proposed.ActionItemId, token).ConfigureAwait(false);
            foreach (var row in transitions) ValidateTransition(row);
            if (transitions.Any(row => row.Sequence == 3) && !transitions.Any(row => row.Sequence == 2))
                return ActionJournalWriteOutcome.InvalidPredecessor;
            var existing = transitions.SingleOrDefault(row => row.Sequence == sequence);
            if (existing is not null) return existing == proposed ? ActionJournalWriteOutcome.ExactRetry : ActionJournalWriteOutcome.Conflict;
            if (sequence == 2 ? transitions.Count != 0 : transitions.Count != 1 || transitions[0].Sequence != 2)
                return ActionJournalWriteOutcome.InvalidPredecessor;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO action_transitions (action_item_id, sequence, transition_kind,
                  recorded_utc, transition_payload, transition_payload_sha256)
                VALUES ($id, $sequence, $kind, $utc, $payload, $hash);
                """;
            command.Parameters.AddWithValue("$id", proposed.ActionItemId);
            command.Parameters.AddWithValue("$sequence", proposed.Sequence);
            command.Parameters.AddWithValue("$kind", proposed.Kind);
            command.Parameters.AddWithValue("$utc", proposed.RecordedUtc);
            command.Parameters.AddWithValue("$payload", proposed.Payload);
            command.Parameters.AddWithValue("$hash", proposed.PayloadSha256);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await owner.AfterActionInsertAsync(connection, transaction, token).ConfigureAwait(false);
            var inserted = await ReadTransitionsAsync(connection, transaction, proposed.ActionItemId, token).ConfigureAwait(false);
            if (inserted.Count != transitions.Count + 1 || inserted.SingleOrDefault(row => row.Sequence == sequence) != proposed
                || transitions.Any(row => !inserted.Contains(row)))
                throw new InvalidDataException("Transition readback mismatch.");
            foreach (var row in inserted) ValidateTransition(row);
            return ActionJournalWriteOutcome.Persisted;
        }, cancellationToken);

    private async ValueTask<ActionJournalWriteResult> WriteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<ActionJournalWriteOutcome>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await owner.RunWriteAsync(SqliteWriteDurability.ActionFull, async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction).ConfigureAwait(false);
                var result = await work(connection, transaction, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result == ActionJournalWriteOutcome.Persisted) await transaction.CommitAsync(token).ConfigureAwait(false);
                return new ActionJournalWriteResult(result);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception)
            || exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // Disposed admission and cancellation remain caller lifecycle signals.
            if (exception is ObjectDisposedException) throw;
            return new(ActionJournalWriteOutcome.Unavailable);
        }
    }

    private static bool ValidRequested(ActionRequestedFact fact)
    {
        if (!ActionJournalPayloadValidator.IsComplete(fact)) return false;
        var safety = fact.Safety;
        if (fact.ActionItemId == Guid.Empty || safety?.Claim?.Request is not { } request || safety.Item is not { } item
            || safety.Claim.ClaimId.Value == Guid.Empty || request.RequestId == Guid.Empty
            || request.Items.IsDefaultOrEmpty || request.Items.Any(value => value is null) || !request.Items.Contains(item)
            || item.Occurrence is null || item.Content is null || request.Scan is null
            || request.Scope is null || request.Disclosure is null
            || item.Occurrence.OccurrenceRevision <= 0 || item.Occurrence.Pin != request.Scan
            || item.Content.PhysicalFileKey != item.PhysicalFileKey
            || safety.Input?.ApprovedGroup is null || safety.Decision is null || safety.Observations.IsDefault || safety.PrecedingOutcomes.IsDefault
            || safety.Observations.Any(value => value is null) || safety.PrecedingOutcomes.Any(value => value is null)
            || safety.Decision.Kind != ActionSafetyDecisionKind.Allowed
            || safety.Input.ApprovedGroup.GroupId != item.GroupId || safety.Input.ApprovedGroup.GroupRevision != item.GroupRevision
            || item.OverrideEvidenceId != fact.OverrideEvidence?.EvidenceId) return false;
        return fact.RequestPayloadSha256 == Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    }

    private static RequestedPayload DecodeItem(ItemRow row)
    {
        var decoded = CanonicalJson.Decode<RequestedPayload>(row.Payload);
        if (!ActionJournalPayloadValidator.IsComplete(decoded.Fact))
            throw new InvalidDataException("Incomplete Requested payload.");
        if (decoded.ActionSafetyPolicyVersion != ActionSafetyPolicy.Version || decoded.ClaimVersion != "review-action-claim-v1"
            || Hash(row.Payload) != row.PayloadSha256 || ItemRow.Create(decoded) != row
            || Hash(decoded.RequestIdentityPayload) != row.RequestPayloadSha256)
            throw new InvalidDataException("Invalid canonical Requested row.");
        var request = JsonSerializer.Deserialize<ApprovedActionRequest>(decoded.RequestIdentityPayload)
            ?? throw new InvalidDataException("Missing request identity.");
        if (CanonicalJson.Encode(request) != CanonicalJson.Encode(decoded.Fact.Safety.Claim.Request)
            || !ValidRequested(decoded.Fact with { Safety = decoded.Fact.Safety with { Claim = decoded.Fact.Safety.Claim with { Request = request } } })
            || decoded.OverridePayload != (decoded.Fact.OverrideEvidence is { } evidence ? SqliteOverrideEvidenceStore.CanonicalPayload(evidence) : null)
            || decoded.OverridePayloadSha256 != (decoded.Fact.OverrideEvidence is { } overridden ? SqliteOverrideEvidenceStore.PayloadHash(overridden) : null))
            throw new InvalidDataException("Requested payload identity mismatch.");
        return decoded;
    }

    private static void ValidateTransition(TransitionRow row) => _ = DecodeTransition(row);

    private static object DecodeTransition(TransitionRow row)
    {
        if (Hash(row.Payload) != row.PayloadSha256) throw new InvalidDataException("Transition hash mismatch.");
        if (row.Sequence == 2)
        {
            var fact = CanonicalJson.Decode<ActionExecutingFact>(row.Payload);
            if (fact.ActionItemId == Guid.Empty || row.ActionItemId != GuidText(fact.ActionItemId)
                || row.Kind != "executing" || row.RecordedUtc != Utc(fact.ExecutingUtc))
                throw new InvalidDataException("Invalid Executing row.");
            return fact;
        }
        else
        {
            var fact = CanonicalJson.Decode<ActionTerminalFact>(row.Payload);
            if (row.Sequence != 3 || !Enum.IsDefined(fact.Kind) || !Enum.IsDefined(fact.Reason) || fact.Evidence is null
                || fact.ActionItemId == Guid.Empty || row.ActionItemId != GuidText(fact.ActionItemId)
                || row.Kind != KindText(fact.Kind) || row.RecordedUtc != Utc(fact.RecordedUtc))
                throw new InvalidDataException("Invalid terminal row.");
            return fact;
        }
    }

    internal static async Task<ImmutableArray<PersistedActionItem>> LoadJournalAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        await ValidateForeignKeysAsync(connection, transaction, token).ConfigureAwait(false);
        var rows = await ReadAllItemsAsync(connection, transaction, token).ConfigureAwait(false);
        var decoded = rows.Select(row => (Row: row, Payload: DecodeItem(row))).ToArray();
        foreach (var request in decoded.GroupBy(value => value.Row.RequestId))
        {
            var identity = request.First();
            if (request.Any(value => value.Row.RequestPayloadSha256 != identity.Row.RequestPayloadSha256
                || value.Payload.RequestIdentityPayload != identity.Payload.RequestIdentityPayload))
                throw new InvalidDataException("Inconsistent request identity.");
        }
        var result = ImmutableArray.CreateBuilder<PersistedActionItem>(decoded.Length);
        foreach (var item in decoded)
        {
            var transitions = await ReadTransitionsAsync(connection, transaction, item.Row.ActionItemId, token).ConfigureAwait(false);
            if (transitions.Count > 2 || transitions.Count >= 1 && transitions[0].Sequence != 2
                || transitions.Count == 2 && transitions[1].Sequence != 3)
                throw new InvalidDataException("Invalid Action transition order.");
            ActionExecutingFact? executing = null;
            ActionTerminalFact? terminal = null;
            foreach (var transition in transitions)
            {
                var fact = DecodeTransition(transition);
                if (fact is ActionExecutingFact executingFact) executing = executingFact;
                else terminal = (ActionTerminalFact)fact;
            }
            result.Add(new(Guid.Parse(item.Row.RequestId), item.Payload.Fact, executing, terminal, []));
        }
        return result.MoveToImmutable();
    }

    private static async Task ValidateForeignKeysAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("SQLite foreign key validation failed.");
    }

    private static async Task<List<ItemRow>> ReadAllItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT action_item_id, request_id, request_payload_sha256, occurrence_id,
              occurrence_revision, requested_utc, requested_payload, requested_payload_sha256
            FROM action_items ORDER BY requested_utc, action_item_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<ItemRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            RequireStorageTypes(reader, 3, 4);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }
        return result;
    }

    private static async Task<List<ItemRow>> ReadItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string itemId, string? requestId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT action_item_id, request_id, request_payload_sha256, occurrence_id,
              occurrence_revision, requested_utc, requested_payload, requested_payload_sha256
            FROM action_items WHERE action_item_id=$id OR request_id=$request ORDER BY action_item_id;
            """;
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$request", (object?)requestId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<ItemRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            RequireStorageTypes(reader, 3, 4);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }
        return result;
    }
    private static async Task<List<TransitionRow>> ReadTransitionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string itemId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT action_item_id, sequence, transition_kind, recorded_utc, transition_payload, transition_payload_sha256
            FROM action_transitions WHERE action_item_id=$id ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$id", itemId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var result = new List<TransitionRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            RequireStorageTypes(reader, 1);
            result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        return result;
    }

    internal static void RequireStorageTypes(SqliteDataReader reader, params int[] integerColumns)
    {
        // SQLite affinity permits REAL values in INTEGER columns. Typed getters
        // coerce those values, which must not erase a changed revision or ID.
        for (var index = 0; index < reader.FieldCount; index++)
            if (integerColumns.Contains(index) ? reader.GetValue(index) is not long : reader.GetValue(index) is not string)
                throw new InvalidDataException("Noncanonical SQLite storage type.");
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string GuidText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
    internal static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static string KindText(ActionTerminalKind kind) => kind switch
    {
        ActionTerminalKind.Recycled => "recycled",
        ActionTerminalKind.NotRemoved => "not_removed",
        ActionTerminalKind.UnknownPendingRevalidation => "unknown_pending_revalidation",
        _ => throw new InvalidDataException("Unknown terminal kind."),
    };

    private sealed record RequestedPayload(ActionRequestedFact Fact, string ActionSafetyPolicyVersion, string ClaimVersion,
        string RequestIdentityPayload, string? OverridePayload, string? OverridePayloadSha256)
    {
        internal static RequestedPayload Create(ActionRequestedFact fact) => new(fact, ActionSafetyPolicy.Version, "review-action-claim-v1",
            JsonSerializer.Serialize(fact.Safety.Claim.Request),
            fact.OverrideEvidence is { } evidence ? SqliteOverrideEvidenceStore.CanonicalPayload(evidence) : null,
            fact.OverrideEvidence is { } overridden ? SqliteOverrideEvidenceStore.PayloadHash(overridden) : null);
    }
    private sealed record ItemRow(string ActionItemId, string RequestId, string RequestPayloadSha256, long OccurrenceId,
        long OccurrenceRevision, string RequestedUtc, string Payload, string PayloadSha256)
    {
        internal static ItemRow Create(RequestedPayload payload)
        {
            var fact = payload.Fact;
            var json = CanonicalJson.Encode(payload);
            return new(GuidText(fact.ActionItemId), GuidText(fact.Safety.Claim.Request.RequestId), fact.RequestPayloadSha256,
                fact.Safety.Item.Occurrence.OccurrenceId, fact.Safety.Item.Occurrence.OccurrenceRevision, Utc(fact.RequestedUtc), json, Hash(json));
        }
    }
    private sealed record TransitionRow(string ActionItemId, int Sequence, string Kind, string RecordedUtc, string Payload, string PayloadSha256);

    internal static class CanonicalJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Converters = { new UtcConverter(), new LongSetConverter() },
        };
        internal static string Encode<T>(T value)
        {
            var element = JsonSerializer.SerializeToElement(value, Options);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        internal static T Decode<T>(string text)
        {
            var value = JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Null payload.");
            if (Encode(value) != text) throw new InvalidDataException("Noncanonical payload.");
            return value;
        }
        private static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if ((property.Name == "sha256" || property.Name == "observedSha256")
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var digest = property.Value.GetString()!;
                        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid content digest.");
                        writer.WriteStringValue(digest.ToUpperInvariant());
                    }
                    else Write(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) Write(writer, child);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
        private sealed class UtcConverter : JsonConverter<DateTimeOffset>
        {
            public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();
            public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(Utc(value));
        }
        private sealed class LongSetConverter : JsonConverter<ImmutableHashSet<long>>
        {
            public override ImmutableHashSet<long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                (JsonSerializer.Deserialize<long[]>(ref reader, options) ?? throw new JsonException("Null set.")).ToImmutableHashSet();
            public override void Write(Utf8JsonWriter writer, ImmutableHashSet<long> value, JsonSerializerOptions options) =>
                JsonSerializer.Serialize(writer, value.Order().ToArray(), options);
        }
    }
}
