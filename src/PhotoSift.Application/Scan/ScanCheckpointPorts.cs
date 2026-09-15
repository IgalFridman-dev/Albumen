using System.Collections.Immutable;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

public sealed record ScanLifecycleTransitionCommand
{
    public ScanLifecycleTransitionCommand(
        Guid scanId,
        long expectedRevision,
        Guid transitionId,
        ScanLifecycleState nextState)
    {
        ValidateIdentity(scanId, expectedRevision, transitionId);
        if (!Enum.IsDefined(nextState))
            throw new ArgumentOutOfRangeException(nameof(nextState), nextState, "Scan lifecycle state must be defined.");
        ScanId = scanId;
        ExpectedRevision = expectedRevision;
        TransitionId = transitionId;
        NextState = nextState;
    }

    public Guid ScanId { get; }

    public long ExpectedRevision { get; }

    public Guid TransitionId { get; }

    public ScanLifecycleState NextState { get; }

    internal static void ValidateIdentity(Guid scanId, long expectedRevision, Guid transitionId)
    {
        if (scanId == Guid.Empty)
            throw new ArgumentException("Scan ID must be nonempty.", nameof(scanId));
        if (expectedRevision <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision), expectedRevision, "Expected revision must be positive.");
        if (transitionId == Guid.Empty)
            throw new ArgumentException("Transition ID must be nonempty.", nameof(transitionId));
    }
}

public sealed record ScanStageTransitionCommand
{
    public ScanStageTransitionCommand(
        Guid scanId,
        long occurrenceId,
        ScanStage stage,
        long expectedRevision,
        Guid transitionId,
        StageOutcome outcome)
    {
        ScanLifecycleTransitionCommand.ValidateIdentity(scanId, expectedRevision, transitionId);
        if (occurrenceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(occurrenceId), occurrenceId, "Occurrence ID must be positive.");
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Scan stage must be defined.");
        ArgumentNullException.ThrowIfNull(outcome);
        ScanId = scanId;
        OccurrenceId = occurrenceId;
        Stage = stage;
        ExpectedRevision = expectedRevision;
        TransitionId = transitionId;
        Outcome = outcome;
    }

    public Guid ScanId { get; }

    public long OccurrenceId { get; }

    public ScanStage Stage { get; }

    public long ExpectedRevision { get; }

    public Guid TransitionId { get; }

    public StageOutcome Outcome { get; }
}

public sealed record InitialOccurrenceCheckpointCommand
{
    public InitialOccurrenceCheckpointCommand(
        ScanOccurrenceIdentityRecord occurrence,
        ScanCheckpoint enumeration,
        ScanCheckpoint exact)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(enumeration);
        ArgumentNullException.ThrowIfNull(exact);

        var token = occurrence.Token;
        if (enumeration.ScanId != token.Pin.ScanId
            || exact.ScanId != token.Pin.ScanId
            || enumeration.OccurrenceId != token.OccurrenceId
            || exact.OccurrenceId != token.OccurrenceId
            || enumeration.Stage != ScanStage.Enumeration
            || exact.Stage != ScanStage.Exact
            || exact.Prerequisite?.Outcome != enumeration.Outcome)
        {
            throw new ArgumentException(
                "Initial checkpoint rows must identify the supplied occurrence and Enumeration/Exact stages.");
        }

        Occurrence = occurrence;
        Enumeration = enumeration;
        Exact = exact;
    }

    public ScanOccurrenceIdentityRecord Occurrence { get; }

    public ScanCheckpoint Enumeration { get; }

    public ScanCheckpoint Exact { get; }
}

public enum ScanCheckpointWriteOutcome
{
    Accepted,
    Idempotent,
    Stale,
    Conflict,
    IllegalTransition,
    Missing,
    Unavailable,
}

public sealed record ScanCheckpointWriteResult
{
    public ScanCheckpointWriteResult(
        ScanCheckpointWriteOutcome outcome,
        ScanCheckpointReadResult? snapshot)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Checkpoint write outcome must be defined.");
        var exposesSnapshot = outcome is ScanCheckpointWriteOutcome.Accepted or ScanCheckpointWriteOutcome.Idempotent;
        if (exposesSnapshot != (snapshot is not null))
            throw new ArgumentException("Accepted and idempotent writes must expose the canonical snapshot.", nameof(snapshot));
        if (snapshot is not null && snapshot.Outcome != ScanCheckpointReadOutcome.Found)
            throw new ArgumentException("A write snapshot must be Found.", nameof(snapshot));
        Outcome = outcome;
        Snapshot = snapshot;
    }

    public ScanCheckpointWriteOutcome Outcome { get; }

    public ScanCheckpointReadResult? Snapshot { get; }
}

public enum ScanCheckpointReadOutcome
{
    Found,
    Missing,
    Corrupt,
    Unavailable,
}

public sealed record ScanCheckpointReadResult
{
    public ScanCheckpointReadResult(
        ScanCheckpointReadOutcome outcome,
        ScanLifecycleCheckpoint? lifecycle,
        ImmutableArray<ScanCheckpointProjection> stages,
        ScanAccounting? accounting)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Checkpoint read outcome must be defined.");
        if (stages.IsDefault)
            throw new ArgumentException("Checkpoint rows must be initialized.", nameof(stages));

        var found = outcome == ScanCheckpointReadOutcome.Found;
        if (found != (lifecycle is not null && accounting is not null)
            || (!found && !stages.IsEmpty))
        {
            throw new ArgumentException(
                "Only a Found read can expose lifecycle, stage, and accounting values.");
        }

        if (found && stages.Any(stage => stage.Stored.ScanId != lifecycle!.ScanId))
            throw new ArgumentException("All checkpoint rows must belong to the lifecycle Scan.", nameof(stages));

        Outcome = outcome;
        Lifecycle = lifecycle;
        Stages = stages;
        Accounting = accounting;
    }

    public ScanCheckpointReadOutcome Outcome { get; }

    public ScanLifecycleCheckpoint? Lifecycle { get; }

    public ImmutableArray<ScanCheckpointProjection> Stages { get; }

    public ScanAccounting? Accounting { get; }
}

public interface IScanCheckpointStore
{
    ValueTask<ScanCheckpointWriteResult> WriteInitialOccurrenceAsync(
        InitialOccurrenceCheckpointCommand command,
        CancellationToken cancellationToken);

    ValueTask<ScanCheckpointWriteResult> TransitionLifecycleAsync(
        ScanLifecycleTransitionCommand command,
        CancellationToken cancellationToken);

    ValueTask<ScanCheckpointWriteResult> TransitionStageAsync(
        ScanStageTransitionCommand command,
        CancellationToken cancellationToken);

    ValueTask<ScanCheckpointReadResult> ReadAsync(
        Guid scanId,
        CancellationToken cancellationToken);
}
