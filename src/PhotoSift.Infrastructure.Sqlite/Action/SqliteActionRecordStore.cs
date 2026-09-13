using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Action;

namespace PhotoSift.Infrastructure.Sqlite.Action;

internal sealed class SqliteActionRecordStore(SqliteDatabaseOwner owner) : IActionRecordStore
{
    public async ValueTask<ActionRecordLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await owner.RunAsync(false, async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: true);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction).ConfigureAwait(false);
                var journal = await SqliteActionJournalStore.LoadJournalAsync(connection, transaction, token).ConfigureAwait(false);
                var rows = await ReadRowsAsync(connection, transaction, token).ConfigureAwait(false);
                var facts = ValidateRows(rows);
                ValidateReferences(journal, facts);
                var byItem = facts.GroupBy(value => value.ActionItemId).ToDictionary(group => group.Key,
                    group => group.ToImmutableArray());
                var items = journal.Select(item =>
                {
                    byItem.TryGetValue(item.Requested.ActionItemId, out var observations);
                    observations = observations.IsDefault ? [] : observations;
                    ValidateResolution(item.Terminal, observations);
                    return item with { Revalidations = observations };
                }).ToImmutableArray();
                return new ActionRecordLoadResult(ActionRecordLoadOutcome.Loaded, items);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Unavailable(exception))
        {
            if (exception is ObjectDisposedException) throw;
            return new(ActionRecordLoadOutcome.Unavailable, []);
        }
    }

    public ValueTask<ActionRevalidationWriteResult> AppendRevalidationAsync(ActionRevalidationFact fact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return WriteAsync(fact, cancellationToken);
    }

    private async ValueTask<ActionRevalidationWriteResult> WriteAsync(ActionRevalidationFact fact,
        CancellationToken cancellationToken)
    {
        try
        {
            return await owner.RunAsync<ActionRevalidationWriteResult>(true, async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction).ConfigureAwait(false);
                if (!Valid(fact)) return new(ActionRevalidationWriteOutcome.Conflict);
                var journal = await SqliteActionJournalStore.LoadJournalAsync(connection, transaction, token).ConfigureAwait(false);
                var rows = await ReadRowsAsync(connection, transaction, token).ConfigureAwait(false);
                var facts = ValidateRows(rows);
                ValidateReferences(journal, facts);
                var canonical = CanonicalFact(fact);
                var existingIndex = rows.FindIndex(row => row.RevalidationId == SqliteActionJournalStore.GuidText(fact.RevalidationId));
                if (existingIndex >= 0)
                    return new(rows[existingIndex] == RevalidationRow.Create(canonical, rows[existingIndex].Sequence)
                        ? ActionRevalidationWriteOutcome.ExactRetry : ActionRevalidationWriteOutcome.Conflict);
                var item = journal.SingleOrDefault(value => value.Requested.ActionItemId == fact.ActionItemId);
                if (item is null) return new(ActionRevalidationWriteOutcome.Conflict);
                if (!MatchesReviewed(item, canonical)) return new(ActionRevalidationWriteOutcome.Conflict);
                var itemFacts = facts.Where(value => value.ActionItemId == fact.ActionItemId).ToImmutableArray();
                ValidateResolution(item.Terminal, itemFacts);
                if (IsResolved(item.Terminal, itemFacts)) return new(ActionRevalidationWriteOutcome.Resolved);
                var proposed = RevalidationRow.Create(canonical, itemFacts.Length + 1);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO action_revalidations (revalidation_id, action_item_id, sequence, observed_utc,
                      result_kind, reason, observation_payload, observation_payload_sha256)
                    VALUES ($revalidation, $item, $sequence, $utc, $kind, $reason, $payload, $hash);
                    """;
                command.Parameters.AddWithValue("$revalidation", proposed.RevalidationId);
                command.Parameters.AddWithValue("$item", proposed.ActionItemId);
                command.Parameters.AddWithValue("$sequence", proposed.Sequence);
                command.Parameters.AddWithValue("$utc", proposed.ObservedUtc);
                command.Parameters.AddWithValue("$kind", proposed.Kind);
                command.Parameters.AddWithValue("$reason", proposed.Reason);
                command.Parameters.AddWithValue("$payload", proposed.Payload);
                command.Parameters.AddWithValue("$hash", proposed.PayloadSha256);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await owner.AfterActionInsertAsync(connection, transaction, token).ConfigureAwait(false);
                var inserted = await ReadRowsAsync(connection, transaction, token).ConfigureAwait(false);
                if (inserted.Count != rows.Count + 1 || inserted.SingleOrDefault(value => value.RevalidationId == proposed.RevalidationId) != proposed
                    || rows.Any(value => !inserted.Contains(value)))
                    throw new InvalidDataException("Revalidation readback mismatch.");
                _ = ValidateRows(inserted);
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new(ActionRevalidationWriteOutcome.Persisted);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Unavailable(exception))
        {
            if (exception is ObjectDisposedException) throw;
            return new(ActionRevalidationWriteOutcome.Unavailable);
        }
    }

    private static List<ActionRevalidationFact> ValidateRows(List<RevalidationRow> rows)
    {
        var result = new List<ActionRevalidationFact>(rows.Count);
        foreach (var group in rows.GroupBy(row => row.ActionItemId))
        {
            var expectedSequence = 1;
            var resolved = false;
            foreach (var row in group.OrderBy(row => row.Sequence))
            {
                if (row.Sequence != expectedSequence++ || resolved)
                    throw new InvalidDataException("Invalid revalidation sequence.");
                var fact = Decode(row);
                result.Add(fact);
                resolved = fact.Kind == ActionRevalidationKind.ConfirmedPresent;
            }
        }
        return result;
    }

    private static ActionRevalidationFact Decode(RevalidationRow row)
    {
        if (SqliteActionJournalStore.Hash(row.Payload) != row.PayloadSha256)
            throw new InvalidDataException("Revalidation hash mismatch.");
        var fact = SqliteActionJournalStore.CanonicalJson.Decode<ActionRevalidationFact>(row.Payload);
        if (!Valid(fact) || RevalidationRow.Create(fact, row.Sequence) != row)
            throw new InvalidDataException("Invalid canonical revalidation row.");
        return fact;
    }

    private static ActionRevalidationFact CanonicalFact(ActionRevalidationFact fact) =>
        SqliteActionJournalStore.CanonicalJson.Decode<ActionRevalidationFact>(
            SqliteActionJournalStore.CanonicalJson.Encode(fact));

    private static void ValidateReferences(ImmutableArray<PersistedActionItem> journal,
        List<ActionRevalidationFact> facts)
    {
        var items = journal.ToDictionary(item => item.Requested.ActionItemId);
        foreach (var fact in facts)
        {
            if (!items.TryGetValue(fact.ActionItemId, out var item))
                throw new InvalidDataException("Revalidation references a missing Action item.");
            if (!MatchesReviewed(item, fact))
                throw new InvalidDataException("Confirmed revalidation does not match reviewed evidence.");
        }
    }

    private static bool MatchesReviewed(PersistedActionItem item, ActionRevalidationFact fact)
    {
        if (fact.Kind != ActionRevalidationKind.ConfirmedPresent) return true;
        var reviewed = item.Requested.Safety.Item;
        return fact.Observation.ObservedKey == reviewed.PhysicalFileKey
            && fact.Observation.ObservedLength == reviewed.Content.ByteLength
            && string.Equals(fact.Observation.ObservedSha256, reviewed.Content.Sha256, StringComparison.Ordinal);
    }

    private static bool Valid(ActionRevalidationFact fact)
    {
        if (fact.RevalidationId == Guid.Empty || fact.ActionItemId == Guid.Empty || fact.Observation is null
            || !Enum.IsDefined(fact.Kind) || !Enum.IsDefined(fact.Reason)
            || fact.Observation.ObservedLength is < 0
            || fact.Observation.ObservedSha256 is { } digest && (digest.Length != 64 || !digest.All(Uri.IsHexDigit)))
            return false;
        return fact.Kind switch
        {
            ActionRevalidationKind.ConfirmedPresent => fact.Reason == ActionRevalidationReason.OriginalFileConfirmed
                && fact.Observation is { PathExists: true, ObservedKey: not null, ObservedLength: not null, ObservedSha256: not null },
            ActionRevalidationKind.StillUnknown => fact.Reason != ActionRevalidationReason.OriginalFileConfirmed,
            _ => false,
        };
    }

    private static bool IsResolved(ActionTerminalFact? terminal, ImmutableArray<ActionRevalidationFact> facts) =>
        terminal?.Kind is ActionTerminalKind.Recycled or ActionTerminalKind.NotRemoved
        || facts.Any(value => value.Kind == ActionRevalidationKind.ConfirmedPresent);

    private static void ValidateResolution(ActionTerminalFact? terminal, ImmutableArray<ActionRevalidationFact> facts)
    {
        if (terminal?.Kind is ActionTerminalKind.Recycled or ActionTerminalKind.NotRemoved && !facts.IsDefaultOrEmpty)
            throw new InvalidDataException("Resolved terminal item has revalidation evidence.");
    }

    private static async Task<List<RevalidationRow>> ReadRowsAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revalidation_id, action_item_id, sequence, observed_utc, result_kind, reason,
              observation_payload, observation_payload_sha256
            FROM action_revalidations ORDER BY action_item_id, sequence;
            """;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<RevalidationRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            SqliteActionJournalStore.RequireStorageTypes(reader, 2);
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        }
        return rows;
    }

    private static bool Unavailable(Exception exception) => SqliteDatabaseOwner.IsUnavailable(exception)
        || exception is JsonException or ArgumentException or InvalidOperationException or OverflowException;

    private sealed record RevalidationRow(string RevalidationId, string ActionItemId, int Sequence,
        string ObservedUtc, string Kind, string Reason, string Payload, string PayloadSha256)
    {
        internal static RevalidationRow Create(ActionRevalidationFact fact, int sequence)
        {
            var payload = SqliteActionJournalStore.CanonicalJson.Encode(fact);
            return new(SqliteActionJournalStore.GuidText(fact.RevalidationId), SqliteActionJournalStore.GuidText(fact.ActionItemId),
                sequence, SqliteActionJournalStore.Utc(fact.ObservedUtc), KindText(fact.Kind), ReasonText(fact.Reason),
                payload, SqliteActionJournalStore.Hash(payload));
        }
    }

    private static string KindText(ActionRevalidationKind kind) => kind switch
    {
        ActionRevalidationKind.ConfirmedPresent => "confirmed_present",
        ActionRevalidationKind.StillUnknown => "still_unknown",
        _ => throw new InvalidDataException("Unknown revalidation kind."),
    };

    private static string ReasonText(ActionRevalidationReason reason) => reason switch
    {
        ActionRevalidationReason.OriginalFileConfirmed => "original_file_confirmed",
        ActionRevalidationReason.OriginalPathMissing => "original_path_missing",
        ActionRevalidationReason.OriginalPathChanged => "original_path_changed",
        ActionRevalidationReason.AccessUnavailable => "access_unavailable",
        ActionRevalidationReason.LocationUnavailable => "location_unavailable",
        ActionRevalidationReason.TimedOut => "timed_out",
        ActionRevalidationReason.Failed => "failed",
        _ => throw new InvalidDataException("Unknown revalidation reason."),
    };
}
