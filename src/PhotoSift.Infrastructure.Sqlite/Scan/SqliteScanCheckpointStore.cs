using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Sqlite.Scan;

internal sealed class SqliteScanCheckpointStore(SqliteDatabaseOwner owner) : IScanCheckpointStore
{
    private static readonly StageOutcome Succeeded =
        new(ScanStageState.Succeeded, ScanReason.None);

    public ValueTask<ScanCheckpointWriteResult> WriteInitialOccurrenceAsync(
        InitialOccurrenceCheckpointCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WriteAsync(async (connection, transaction, token) =>
        {
            var scanId = GuidText(command.Occurrence.Token.Pin.ScanId);
            var parent = await ReadScanPinAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            if (parent is null) return Rejected(ScanCheckpointWriteOutcome.Missing);
            if (parent != command.Occurrence.Token.Pin)
                return Rejected(ScanCheckpointWriteOutcome.Conflict);
            if (await ReadLifecycleRowAsync(connection, transaction, scanId, token).ConfigureAwait(false) is not { } lifecycle)
                return Rejected(ScanCheckpointWriteOutcome.Missing);
            _ = DecodeLifecycle(lifecycle);

            var existingOccurrence = await ReadOccurrenceRowAsync(
                connection, transaction, scanId, command.Occurrence.Token.OccurrenceId, token).ConfigureAwait(false);
            if (existingOccurrence is not null)
            {
                var rows = await ReadStageRowsAsync(
                    connection, transaction, scanId, command.Occurrence.Token.OccurrenceId, token).ConfigureAwait(false);
                var exactRetry = TryDecodeOccurrence(existingOccurrence, parent, out var occurrence)
                    && occurrence == command.Occurrence
                    && rows.Count == 2
                    && TryDecodeStage(rows[0], out var first)
                    && TryDecodeStage(rows[1], out var second)
                    && first == command.Enumeration
                    && second == command.Exact;
                return exactRetry
                    ? await ExistingAsync(connection, transaction, scanId, token).ConfigureAwait(false)
                    : Rejected(ScanCheckpointWriteOutcome.Conflict);
            }

            await InsertOccurrenceAsync(connection, transaction, command.Occurrence, token).ConfigureAwait(false);
            await InsertStageAsync(connection, transaction, command.Enumeration, token).ConfigureAwait(false);
            await InsertStageAsync(connection, transaction, command.Exact, token).ConfigureAwait(false);
            await owner.AfterCheckpointAuthoritativeWriteAsync(connection, transaction, token).ConfigureAwait(false);
            await RecomputeAndWriteAccountingAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            return await AcceptedAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public ValueTask<ScanCheckpointWriteResult> TransitionLifecycleAsync(
        ScanLifecycleTransitionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WriteAsync(async (connection, transaction, token) =>
        {
            var scanId = GuidText(command.ScanId);
            if (await ReadScanPinAsync(connection, transaction, scanId, token).ConfigureAwait(false) is null)
                return Rejected(ScanCheckpointWriteOutcome.Missing);
            var row = await ReadLifecycleRowAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            if (row is null)
            {
                if (command.ExpectedRevision != 1 || command.NextState != ScanLifecycleState.Preflight)
                    return Rejected(ScanCheckpointWriteOutcome.Missing);
                var initial = new ScanLifecycleCheckpoint(
                    command.ScanId, 1, command.TransitionId, ScanLifecycleState.Preflight);
                await InsertLifecycleAsync(connection, transaction, initial, token).ConfigureAwait(false);
                await owner.AfterCheckpointAuthoritativeWriteAsync(connection, transaction, token).ConfigureAwait(false);
                await WriteAccountingAsync(
                    connection, transaction, scanId, new ScanAccounting(0, 0, 0, 0, 0, 0), token).ConfigureAwait(false);
                return await AcceptedAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            }

            var current = DecodeLifecycle(row);
            if (current.TransitionId == command.TransitionId)
            {
                if (current.State != command.NextState)
                    return Rejected(ScanCheckpointWriteOutcome.Conflict);
                var exactBootstrapRetry = current.Revision == 1
                    && command.ExpectedRevision == 1
                    && command.NextState == ScanLifecycleState.Preflight;
                var exactTransitionRetry = command.ExpectedRevision < long.MaxValue
                    && current.Revision == command.ExpectedRevision + 1;
                return exactBootstrapRetry || exactTransitionRetry
                    ? await ExistingAsync(connection, transaction, scanId, token).ConfigureAwait(false)
                    : Rejected(ScanCheckpointWriteOutcome.Conflict);
            }
            if (current.Revision != command.ExpectedRevision)
                return Rejected(ScanCheckpointWriteOutcome.Stale);
            if (!ScanTransitionPolicy.CanTransition(current.State, command.NextState))
                return Rejected(ScanCheckpointWriteOutcome.IllegalTransition);
            if (command.NextState == ScanLifecycleState.Complete
                && !await CanCompleteAsync(connection, transaction, scanId, current, token).ConfigureAwait(false))
            {
                return Rejected(ScanCheckpointWriteOutcome.IllegalTransition);
            }

            var updated = new ScanLifecycleCheckpoint(
                command.ScanId, CheckedIncrement(current.Revision), command.TransitionId, command.NextState);
            await UpdateLifecycleAsync(connection, transaction, updated, token).ConfigureAwait(false);
            await owner.AfterCheckpointAuthoritativeWriteAsync(connection, transaction, token).ConfigureAwait(false);
            await RecomputeAndWriteAccountingAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            return await AcceptedAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public ValueTask<ScanCheckpointWriteResult> TransitionStageAsync(
        ScanStageTransitionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return WriteAsync(async (connection, transaction, token) =>
        {
            var scanId = GuidText(command.ScanId);
            var parent = await ReadScanPinAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            if (parent is null
                || await ReadLifecycleRowAsync(connection, transaction, scanId, token).ConfigureAwait(false) is null)
                return Rejected(ScanCheckpointWriteOutcome.Missing);
            var occurrenceRow = await ReadOccurrenceRowAsync(
                connection, transaction, scanId, command.OccurrenceId, token).ConfigureAwait(false);
            if (occurrenceRow is null) return Rejected(ScanCheckpointWriteOutcome.Missing);
            if (!TryDecodeOccurrence(occurrenceRow, parent, out _))
                return Rejected(ScanCheckpointWriteOutcome.Conflict);
            var row = await ReadStageRowAsync(
                connection, transaction, scanId, command.OccurrenceId, command.Stage, token).ConfigureAwait(false);
            if (row is null) return Rejected(ScanCheckpointWriteOutcome.Missing);
            var current = DecodeStage(row);

            if (current.TransitionId == command.TransitionId)
            {
                if (current.Outcome != command.Outcome)
                    return Rejected(ScanCheckpointWriteOutcome.Conflict);
                var exactRetry = command.ExpectedRevision < long.MaxValue
                    && current.Revision == command.ExpectedRevision + 1;
                return exactRetry
                    ? await ExistingAsync(connection, transaction, scanId, token).ConfigureAwait(false)
                    : Rejected(ScanCheckpointWriteOutcome.Conflict);
            }
            if (current.Revision != command.ExpectedRevision)
                return Rejected(ScanCheckpointWriteOutcome.Stale);
            if (!ScanTransitionPolicy.CanTransition(command.Stage, current.Outcome, command.Outcome))
                return Rejected(ScanCheckpointWriteOutcome.IllegalTransition);

            var updated = new ScanCheckpoint(
                command.ScanId, command.OccurrenceId, command.Stage,
                CheckedIncrement(current.Revision), command.TransitionId, command.Outcome, current.Prerequisite);
            await UpdateStageAsync(connection, transaction, updated, token).ConfigureAwait(false);
            await owner.AfterCheckpointAuthoritativeWriteAsync(connection, transaction, token).ConfigureAwait(false);
            await RecomputeAndWriteAccountingAsync(connection, transaction, scanId, token).ConfigureAwait(false);
            return await AcceptedAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async ValueTask<ScanCheckpointReadResult> ReadAsync(
        Guid scanId,
        CancellationToken cancellationToken)
    {
        if (scanId == Guid.Empty) throw new ArgumentException("Scan ID must be nonempty.", nameof(scanId));
        try
        {
            var inspection = await owner.RunReadAsync(async (connection, token) =>
            {
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token).ConfigureAwait(false);
                try
                {
                    var candidate = await ReadSnapshotCandidateAsync(
                        connection, null, GuidText(scanId), token).ConfigureAwait(false);
                    if (candidate is null)
                        return new ReadInspection(EmptyRead(ScanCheckpointReadOutcome.Missing), false);
                    return candidate.StoredAccounting is { } stored
                        && stored == candidate.RecomputedAccounting
                        ? new ReadInspection(Project(candidate, recoverInterruptions: true), false)
                        : new ReadInspection(null, true);
                }
                catch (Exception exception) when (exception is InvalidDataException || IsMalformedValue(exception))
                {
                    return new ReadInspection(EmptyRead(ScanCheckpointReadOutcome.Corrupt), false);
                }
            }, cancellationToken).ConfigureAwait(false);

            if (!inspection.RepairRequired) return inspection.Result!;
            return await RepairAccountingAsync(GuidText(scanId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception))
        {
            return EmptyRead(ScanCheckpointReadOutcome.Unavailable);
        }
    }

    private async ValueTask<ScanCheckpointWriteResult> WriteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<ScanCheckpointWriteResult>> work,
        CancellationToken cancellationToken)
    {
        try
        {
            return await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction)
                    .ConfigureAwait(false);
                var result = await work(connection, transaction, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Outcome == ScanCheckpointWriteOutcome.Accepted)
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception)
            || IsMalformedValue(exception))
        {
            return Rejected(ScanCheckpointWriteOutcome.Unavailable);
        }
    }

    private static async Task<ScanCheckpointWriteResult> AcceptedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string scanId, CancellationToken token)
    {
        var snapshot = await RequireFoundAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        return new(ScanCheckpointWriteOutcome.Accepted, snapshot);
    }

    private static async Task<ScanCheckpointWriteResult> ExistingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string scanId, CancellationToken token)
    {
        var snapshot = await RequireFoundAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        return new(ScanCheckpointWriteOutcome.Idempotent, snapshot);
    }

    private static async Task<ScanCheckpointReadResult> RequireFoundAsync(
        SqliteConnection connection, SqliteTransaction transaction, string scanId, CancellationToken token)
    {
        var candidate = await ReadSnapshotCandidateAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        return candidate is not null
            && candidate.StoredAccounting is { } stored
            && stored == candidate.RecomputedAccounting
            ? Project(candidate, recoverInterruptions: false)
            : throw new InvalidDataException("Checkpoint write did not read back a canonical snapshot.");
    }

    private static async Task<SnapshotCandidate?> ReadSnapshotCandidateAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId, CancellationToken token)
    {
        var parent = await ReadScanPinAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        if (parent is null) return null;
        var lifecycleRow = await ReadLifecycleRowAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        if (lifecycleRow is null)
            throw new InvalidDataException("Existing Scan is missing its checkpoint lifecycle.");
        var lifecycle = DecodeLifecycle(lifecycleRow);
        var stageRows = await ReadStageRowsAsync(connection, transaction, scanId, null, token).ConfigureAwait(false);
        var occurrenceRows = await ReadOccurrenceRowsAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        var occurrences = new Dictionary<long, ScanOccurrenceIdentityRecord>();
        foreach (var row in occurrenceRows)
        {
            if (!TryDecodeOccurrence(row, parent, out var occurrence)
                || !occurrences.TryAdd(occurrence.Token.OccurrenceId, occurrence))
                throw new InvalidDataException("Checkpoint occurrence identity is not canonical.");
        }
        var checkpoints = ImmutableArray.CreateBuilder<ScanCheckpoint>(stageRows.Count);
        foreach (var row in stageRows)
        {
            if (!occurrences.ContainsKey(row.OccurrenceId))
                throw new InvalidDataException("Checkpoint occurrence identity is not canonical.");
            checkpoints.Add(DecodeStage(row));
        }
        var accountingRow = await ReadAccountingRowAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        var accounting = accountingRow is null ? null : DecodeAccounting(accountingRow);
        var values = checkpoints.MoveToImmutable();
        if ((long)values.Length != checked((long)occurrences.Count * 2))
            throw new InvalidDataException("Every persisted occurrence must have its complete required-stage set.");
        var recomputed = Recompute(values);
        return new(lifecycle, values, accounting, recomputed);
    }

    private async ValueTask<ScanCheckpointReadResult> RepairAccountingAsync(
        string scanId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await owner.RunWriteAsync(SqliteWriteDurability.Normal, async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await PhotoSiftSchema.ValidateAsync(connection, PhotoSiftSchema.Version, token, transaction)
                    .ConfigureAwait(false);
                SnapshotCandidate? candidate;
                try
                {
                    candidate = await ReadSnapshotCandidateAsync(connection, transaction, scanId, token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidDataException || IsMalformedValue(exception))
                {
                    return EmptyRead(ScanCheckpointReadOutcome.Corrupt);
                }

                if (candidate is null) return EmptyRead(ScanCheckpointReadOutcome.Missing);
                await WriteAccountingAsync(
                    connection, transaction, scanId, candidate.RecomputedAccounting, token).ConfigureAwait(false);
                var repaired = await ReadSnapshotCandidateAsync(connection, transaction, scanId, token)
                    .ConfigureAwait(false);
                if (repaired is null
                    || repaired.StoredAccounting is not { } repairedAccounting
                    || repairedAccounting != repaired.RecomputedAccounting)
                    throw new InvalidDataException("Checkpoint accounting repair did not read back canonically.");
                token.ThrowIfCancellationRequested();
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return Project(repaired, recoverInterruptions: true);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteDatabaseOwner.IsUnavailable(exception)
            || IsMalformedValue(exception))
        {
            return EmptyRead(ScanCheckpointReadOutcome.Unavailable);
        }
    }

    private static async Task<bool> CanCompleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scanId,
        ScanLifecycleCheckpoint current,
        CancellationToken token)
    {
        SnapshotCandidate? candidate;
        try
        {
            candidate = await ReadSnapshotCandidateAsync(connection, transaction, scanId, token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException || IsMalformedValue(exception))
        {
            return false;
        }

        if (candidate is null
            || candidate.Lifecycle != current
            || candidate.StoredAccounting is not { } storedAccounting
            || storedAccounting != candidate.RecomputedAccounting)
        {
            return false;
        }

        var enumerationRows = candidate.Stages
            .Where(checkpoint => checkpoint.Stage == ScanStage.Enumeration)
            .ToArray();
        return enumerationRows.Length > 0
            && enumerationRows.All(checkpoint => checkpoint.Outcome.State is
                ScanStageState.Succeeded or ScanStageState.Unsupported)
            && ScanTransitionPolicy.CanComplete(Succeeded, candidate.Stages, storedAccounting);
    }

    private static ScanCheckpointReadResult Project(
        SnapshotCandidate candidate,
        bool recoverInterruptions)
    {
        var projections = candidate.Stages.Select(value => recoverInterruptions
                && value.Outcome.State == ScanStageState.Running
            ? new ScanCheckpointProjection(
                value,
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
                recoveredFromInterruption: true)
            : new ScanCheckpointProjection(value, value.Outcome, recoveredFromInterruption: false))
            .ToImmutableArray();
        return new(
            ScanCheckpointReadOutcome.Found,
            candidate.Lifecycle,
            projections,
            candidate.RecomputedAccounting);
    }

    private static async Task RecomputeAndWriteAccountingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string scanId, CancellationToken token)
    {
        var rows = await ReadStageRowsAsync(connection, transaction, scanId, null, token).ConfigureAwait(false);
        var checkpoints = rows.Select(DecodeStage).ToArray();
        await WriteAccountingAsync(connection, transaction, scanId, Recompute(checkpoints), token).ConfigureAwait(false);
    }

    private static ScanAccounting Recompute(IReadOnlyCollection<ScanCheckpoint> checkpoints)
    {
        if (checkpoints.Count == 0) return new(0, 0, 0, 0, 0, 0);
        return ScanTransitionPolicy.TryRecomputeAccounting(checkpoints, out var accounting)
            ? accounting
            : throw new InvalidDataException("Checkpoint stage rows cannot produce canonical accounting.");
    }

    private static async Task WriteAccountingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scanId,
        ScanAccounting accounting,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_accounting (
                scan_id, discovered, analyzed, unsupported, failed, canceled, outstanding)
            VALUES ($scan_id, $discovered, $analyzed, $unsupported, $failed, $canceled, $outstanding)
            ON CONFLICT(scan_id) DO UPDATE SET
                discovered=excluded.discovered,
                analyzed=excluded.analyzed,
                unsupported=excluded.unsupported,
                failed=excluded.failed,
                canceled=excluded.canceled,
                outstanding=excluded.outstanding;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        command.Parameters.AddWithValue("$discovered", accounting.Discovered);
        command.Parameters.AddWithValue("$analyzed", accounting.Analyzed);
        command.Parameters.AddWithValue("$unsupported", accounting.Unsupported);
        command.Parameters.AddWithValue("$failed", accounting.Failed);
        command.Parameters.AddWithValue("$canceled", accounting.Canceled);
        command.Parameters.AddWithValue("$outstanding", accounting.Outstanding);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var readback = await ReadAccountingRowAsync(connection, transaction, scanId, token).ConfigureAwait(false);
        if (readback is null || DecodeAccounting(readback) != accounting)
            throw new InvalidDataException("SQLite did not retain canonical checkpoint accounting.");
    }

    private static async Task InsertLifecycleAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ScanLifecycleCheckpoint checkpoint, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_lifecycle (scan_id, revision, transition_id, state)
            VALUES ($scan_id, $revision, $transition_id, $state);
            """;
        BindLifecycle(command, checkpoint);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var row = await ReadLifecycleRowAsync(
            connection, transaction, GuidText(checkpoint.ScanId), token).ConfigureAwait(false);
        if (row is null || DecodeLifecycle(row) != checkpoint)
            throw new InvalidDataException("SQLite did not retain the canonical Scan lifecycle.");
    }

    private static async Task UpdateLifecycleAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ScanLifecycleCheckpoint checkpoint, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE scan_lifecycle
            SET revision=$revision, transition_id=$transition_id, state=$state
            WHERE scan_id=$scan_id;
            """;
        BindLifecycle(command, checkpoint);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Scan lifecycle update did not affect exactly one row.");
        var row = await ReadLifecycleRowAsync(
            connection, transaction, GuidText(checkpoint.ScanId), token).ConfigureAwait(false);
        if (row is null || DecodeLifecycle(row) != checkpoint)
            throw new InvalidDataException("SQLite did not retain the canonical Scan lifecycle transition.");
    }

    private static void BindLifecycle(SqliteCommand command, ScanLifecycleCheckpoint checkpoint)
    {
        command.Parameters.AddWithValue("$scan_id", GuidText(checkpoint.ScanId));
        command.Parameters.AddWithValue("$revision", checkpoint.Revision);
        command.Parameters.AddWithValue("$transition_id", GuidText(checkpoint.TransitionId));
        command.Parameters.AddWithValue("$state", (long)checkpoint.State);
    }

    private static async Task InsertOccurrenceAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ScanOccurrenceIdentityRecord occurrence, CancellationToken token)
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
        var physical = occurrence.PhysicalFileKey;
        command.Parameters.AddWithValue("$scan_id", GuidText(occurrence.Token.Pin.ScanId));
        command.Parameters.AddWithValue("$occurrence_id", occurrence.Token.OccurrenceId);
        command.Parameters.AddWithValue("$occurrence_revision", occurrence.Token.OccurrenceRevision);
        command.Parameters.AddWithValue("$attempt", occurrence.Token.Attempt);
        command.Parameters.AddWithValue("$discovered_path", occurrence.DiscoveredPath);
        command.Parameters.AddWithValue("$resolved_path", occurrence.ResolvedPath);
        command.Parameters.AddWithValue("$volume_serial", physical is { } volume ? Hex(volume.VolumeSerial) : DBNull.Value);
        command.Parameters.AddWithValue("$file_id_high", physical is { } high ? Hex(high.FileId.High) : DBNull.Value);
        command.Parameters.AddWithValue("$file_id_low", physical is { } low ? Hex(low.FileId.Low) : DBNull.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var row = await ReadOccurrenceRowAsync(connection, transaction,
            GuidText(occurrence.Token.Pin.ScanId), occurrence.Token.OccurrenceId, token).ConfigureAwait(false);
        if (row is null || !TryDecodeOccurrence(row, occurrence.Token.Pin, out var decoded) || decoded != occurrence)
            throw new InvalidDataException("SQLite did not retain the canonical occurrence identity.");
    }

    private static async Task InsertStageAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ScanCheckpoint checkpoint, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_stage_checkpoints (
                scan_id, occurrence_id, stage, revision, transition_id, state, reason,
                prerequisite_stage, prerequisite_state, prerequisite_reason)
            VALUES (
                $scan_id, $occurrence_id, $stage, $revision, $transition_id, $state, $reason,
                $prerequisite_stage, $prerequisite_state, $prerequisite_reason);
            """;
        BindStage(command, checkpoint);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var row = await ReadStageRowAsync(connection, transaction, GuidText(checkpoint.ScanId),
            checkpoint.OccurrenceId, checkpoint.Stage, token).ConfigureAwait(false);
        if (row is null || DecodeStage(row) != checkpoint)
            throw new InvalidDataException("SQLite did not retain the canonical stage checkpoint.");
    }

    private static async Task UpdateStageAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ScanCheckpoint checkpoint, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE scan_stage_checkpoints
            SET revision=$revision, transition_id=$transition_id, state=$state, reason=$reason,
                prerequisite_stage=$prerequisite_stage,
                prerequisite_state=$prerequisite_state,
                prerequisite_reason=$prerequisite_reason
            WHERE scan_id=$scan_id AND occurrence_id=$occurrence_id AND stage=$stage;
            """;
        BindStage(command, checkpoint);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Stage checkpoint update did not affect exactly one row.");
        var row = await ReadStageRowAsync(connection, transaction, GuidText(checkpoint.ScanId),
            checkpoint.OccurrenceId, checkpoint.Stage, token).ConfigureAwait(false);
        if (row is null || DecodeStage(row) != checkpoint)
            throw new InvalidDataException("SQLite did not retain the canonical stage transition.");
    }

    private static void BindStage(SqliteCommand command, ScanCheckpoint checkpoint)
    {
        var prerequisite = checkpoint.Prerequisite;
        command.Parameters.AddWithValue("$scan_id", GuidText(checkpoint.ScanId));
        command.Parameters.AddWithValue("$occurrence_id", checkpoint.OccurrenceId);
        command.Parameters.AddWithValue("$stage", (long)checkpoint.Stage);
        command.Parameters.AddWithValue("$revision", checkpoint.Revision);
        command.Parameters.AddWithValue("$transition_id", GuidText(checkpoint.TransitionId));
        command.Parameters.AddWithValue("$state", (long)checkpoint.Outcome.State);
        command.Parameters.AddWithValue("$reason", (long)checkpoint.Outcome.Reason);
        command.Parameters.AddWithValue("$prerequisite_stage",
            prerequisite is null ? DBNull.Value : (long)prerequisite.Stage);
        command.Parameters.AddWithValue("$prerequisite_state",
            prerequisite is null ? DBNull.Value : (long)prerequisite.Outcome.State);
        command.Parameters.AddWithValue("$prerequisite_reason",
            prerequisite is null ? DBNull.Value : (long)prerequisite.Outcome.Reason);
    }

    private static async Task<ScanPin?> ReadScanPinAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, scope_revision, rule_set_revision, analysis_revision
            FROM scans WHERE scan_id=$scan_id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var storedId = reader.GetString(0);
        var scope = reader.GetInt64(1);
        var rules = reader.GetInt64(2);
        var analysis = reader.GetInt64(3);
        if (await reader.ReadAsync(token).ConfigureAwait(false)
            || !TryGuid(storedId, out var decodedId)
            || scope <= 0 || rules <= 0 || analysis <= 0)
            throw new InvalidDataException("Parent Scan identity is not canonical.");
        return new(decodedId, scope, rules, analysis);
    }

    private static async Task<StoredLifecycleRow?> ReadLifecycleRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, revision, transition_id, state
            FROM scan_lifecycle WHERE scan_id=$scan_id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var row = new StoredLifecycleRow(
            reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
        if (await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("Multiple lifecycle rows differ only by canonical Scan ID text.");
        return row;
    }

    private static async Task<StoredOccurrenceRow?> ReadOccurrenceRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        string scanId, long occurrenceId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, occurrence_id, occurrence_revision, attempt,
                   discovered_path, resolved_path, volume_serial, file_id_high, file_id_low
            FROM scan_file_occurrences
            WHERE scan_id=$scan_id COLLATE NOCASE AND occurrence_id=$occurrence_id;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        command.Parameters.AddWithValue("$occurrence_id", occurrenceId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var row = new StoredOccurrenceRow(
            reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), NullableString(reader, 6),
            NullableString(reader, 7), NullableString(reader, 8));
        if (await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException(
                "Multiple occurrence rows differ only by canonical Scan ID text.");
        return row;
    }

    private static async Task<List<StoredOccurrenceRow>> ReadOccurrenceRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, occurrence_id, occurrence_revision, attempt,
                   discovered_path, resolved_path, volume_serial, file_id_high, file_id_low
            FROM scan_file_occurrences
            WHERE scan_id=$scan_id COLLATE NOCASE
            ORDER BY scan_id, occurrence_id;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<StoredOccurrenceRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetString(5), NullableString(reader, 6),
                NullableString(reader, 7), NullableString(reader, 8)));
        return rows;
    }

    private static async Task<StoredStageRow?> ReadStageRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId,
        long occurrenceId, ScanStage stage, CancellationToken token)
    {
        var rows = await ReadStageRowsAsync(connection, transaction, scanId, occurrenceId, token).ConfigureAwait(false);
        return rows.SingleOrDefault(row => row.Stage == (long)stage);
    }

    private static async Task<List<StoredStageRow>> ReadStageRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId,
        long? occurrenceId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, occurrence_id, stage, revision, transition_id, state, reason,
                   prerequisite_stage, prerequisite_state, prerequisite_reason
            FROM scan_stage_checkpoints
            WHERE scan_id=$scan_id COLLATE NOCASE
            """ + (occurrenceId is null ? string.Empty : " AND occurrence_id=$occurrence_id")
            + " ORDER BY scan_id, occurrence_id, stage;";
        command.Parameters.AddWithValue("$scan_id", scanId);
        if (occurrenceId is not null) command.Parameters.AddWithValue("$occurrence_id", occurrenceId.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<StoredStageRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6),
                NullableLong(reader, 7), NullableLong(reader, 8), NullableLong(reader, 9)));
        return rows;
    }

    private static async Task<StoredAccountingRow?> ReadAccountingRowAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string scanId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT scan_id, discovered, analyzed, unsupported, failed, canceled, outstanding
            FROM scan_accounting WHERE scan_id=$scan_id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$scan_id", scanId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var row = new StoredAccountingRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
        if (await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("Multiple accounting rows differ only by canonical Scan ID text.");
        return row;
    }

    private static ScanLifecycleCheckpoint DecodeLifecycle(StoredLifecycleRow row)
    {
        if (!TryGuid(row.ScanId, out var scanId) || !TryGuid(row.TransitionId, out var transitionId)
            || !Enum.IsDefined((ScanLifecycleState)row.State))
            throw new InvalidDataException("Stored Scan lifecycle is not canonical.");
        var decoded = new ScanLifecycleCheckpoint(scanId, row.Revision, transitionId, (ScanLifecycleState)row.State);
        if (LifecycleRow(decoded) != row)
            throw new InvalidDataException("Stored Scan lifecycle is not canonical.");
        return decoded;
    }

    private static ScanCheckpoint DecodeStage(StoredStageRow row)
    {
        if (!TryDecodeStage(row, out var checkpoint))
            throw new InvalidDataException("Stored stage checkpoint is not canonical.");
        return checkpoint;
    }

    private static bool TryDecodeStage(StoredStageRow row, out ScanCheckpoint checkpoint)
    {
        checkpoint = null!;
        try
        {
            if (!TryGuid(row.ScanId, out var scanId) || !TryGuid(row.TransitionId, out var transitionId)
                || !Enum.IsDefined((ScanStage)row.Stage)
                || !Enum.IsDefined((ScanStageState)row.State)
                || !Enum.IsDefined((ScanReason)row.Reason))
                return false;
            var allNull = row.PrerequisiteStage is null
                && row.PrerequisiteState is null
                && row.PrerequisiteReason is null;
            var allPresent = row.PrerequisiteStage is not null
                && row.PrerequisiteState is not null
                && row.PrerequisiteReason is not null;
            if (!allNull && !allPresent) return false;
            ScanStagePrerequisite? prerequisite = null;
            if (allPresent)
            {
                if (!Enum.IsDefined((ScanStage)row.PrerequisiteStage!.Value)
                    || !Enum.IsDefined((ScanStageState)row.PrerequisiteState!.Value)
                    || !Enum.IsDefined((ScanReason)row.PrerequisiteReason!.Value))
                    return false;
                prerequisite = new((ScanStage)row.PrerequisiteStage.Value,
                    new((ScanStageState)row.PrerequisiteState.Value, (ScanReason)row.PrerequisiteReason.Value));
            }
            checkpoint = new(scanId, row.OccurrenceId, (ScanStage)row.Stage, row.Revision,
                transitionId, new((ScanStageState)row.State, (ScanReason)row.Reason), prerequisite);
            return StageRow(checkpoint) == row;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ScanAccounting DecodeAccounting(StoredAccountingRow row)
    {
        if (!TryGuid(row.ScanId, out _))
            throw new InvalidDataException("Stored accounting Scan ID is not canonical.");
        return new(row.Discovered, row.Analyzed, row.Unsupported, row.Failed, row.Canceled, row.Outstanding);
    }

    private static bool TryDecodeOccurrence(StoredOccurrenceRow row, ScanPin parent, out ScanOccurrenceIdentityRecord occurrence)
    {
        occurrence = null!;
        try
        {
            if (!TryGuid(row.ScanId, out var scanId) || scanId != parent.ScanId
                || row.OccurrenceId <= 0 || row.OccurrenceRevision <= 0 || row.Attempt <= 0)
                return false;
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
            occurrence = new(new(parent, row.OccurrenceId, row.OccurrenceRevision, row.Attempt),
                row.DiscoveredPath, row.ResolvedPath, physical);
            return OccurrenceRow(occurrence) == row;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static StoredLifecycleRow LifecycleRow(ScanLifecycleCheckpoint checkpoint) => new(
        GuidText(checkpoint.ScanId), checkpoint.Revision, GuidText(checkpoint.TransitionId), (long)checkpoint.State);

    private static StoredStageRow StageRow(ScanCheckpoint checkpoint) => new(
        GuidText(checkpoint.ScanId), checkpoint.OccurrenceId, (long)checkpoint.Stage, checkpoint.Revision,
        GuidText(checkpoint.TransitionId), (long)checkpoint.Outcome.State, (long)checkpoint.Outcome.Reason,
        checkpoint.Prerequisite is { } stagePrerequisite ? (long)stagePrerequisite.Stage : null,
        checkpoint.Prerequisite is { } statePrerequisite ? (long)statePrerequisite.Outcome.State : null,
        checkpoint.Prerequisite is { } reasonPrerequisite ? (long)reasonPrerequisite.Outcome.Reason : null);

    private static StoredOccurrenceRow OccurrenceRow(ScanOccurrenceIdentityRecord occurrence)
    {
        var physical = occurrence.PhysicalFileKey;
        return new(GuidText(occurrence.Token.Pin.ScanId), occurrence.Token.OccurrenceId,
            occurrence.Token.OccurrenceRevision, occurrence.Token.Attempt,
            occurrence.DiscoveredPath, occurrence.ResolvedPath,
            physical is { } volume ? Hex(volume.VolumeSerial) : null,
            physical is { } high ? Hex(high.FileId.High) : null,
            physical is { } low ? Hex(low.FileId.Low) : null);
    }

    private static ScanCheckpointWriteResult Rejected(ScanCheckpointWriteOutcome outcome) => new(outcome, null);

    private static ScanCheckpointReadResult EmptyRead(ScanCheckpointReadOutcome outcome) =>
        new(outcome, null, ImmutableArray<ScanCheckpointProjection>.Empty, null);

    private static long CheckedIncrement(long revision)
    {
        try { return checked(revision + 1); }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Checkpoint revision overflowed.", exception);
        }
    }

    private static bool IsMalformedValue(Exception exception) => exception is InvalidCastException
        or FormatException or OverflowException or ArgumentException or InvalidOperationException;
    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? NullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static string GuidText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();
    private static bool TryGuid(string text, out Guid value) => Guid.TryParseExact(text, "D", out value)
        && GuidText(value).Equals(text, StringComparison.Ordinal);
    private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
    private static bool TryHex(string text, out ulong value) => ulong.TryParse(
        text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
        && Hex(value).Equals(text, StringComparison.Ordinal);

    private sealed record StoredLifecycleRow(string ScanId, long Revision, string TransitionId, long State);
    private sealed record ReadInspection(ScanCheckpointReadResult? Result, bool RepairRequired);
    private sealed record SnapshotCandidate(
        ScanLifecycleCheckpoint Lifecycle,
        ImmutableArray<ScanCheckpoint> Stages,
        ScanAccounting? StoredAccounting,
        ScanAccounting RecomputedAccounting);
    private sealed record StoredStageRow(
        string ScanId, long OccurrenceId, long Stage, long Revision, string TransitionId,
        long State, long Reason, long? PrerequisiteStage, long? PrerequisiteState, long? PrerequisiteReason);
    private sealed record StoredAccountingRow(
        string ScanId, long Discovered, long Analyzed, long Unsupported, long Failed, long Canceled, long Outstanding);
    private sealed record StoredOccurrenceRow(
        string ScanId, long OccurrenceId, long OccurrenceRevision, long Attempt,
        string DiscoveredPath, string ResolvedPath, string? VolumeSerial, string? FileIdHigh, string? FileIdLow);
}
