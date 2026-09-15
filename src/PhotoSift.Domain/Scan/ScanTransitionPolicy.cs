namespace PhotoSift.Domain.Scan;

public enum ScanLifecycleState
{
    Preflight,
    Enumerating,
    Exact,
    Complete,
    Pausing,
    Paused,
    Incomplete,
    Faulted,
}

public enum ScanStage
{
    Enumeration,
    Exact,
}

public sealed record ScanStagePrerequisite
{
    public ScanStagePrerequisite(ScanStage stage, StageOutcome outcome)
    {
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Scan stage must be defined.");
        ArgumentNullException.ThrowIfNull(outcome);
        Stage = stage;
        Outcome = outcome;
    }

    public ScanStage Stage { get; }

    public StageOutcome Outcome { get; }
}

public sealed record ScanLifecycleCheckpoint
{
    public ScanLifecycleCheckpoint(
        Guid scanId,
        long revision,
        Guid transitionId,
        ScanLifecycleState state)
    {
        ValidateScanId(scanId);
        ValidateRevision(revision);
        ValidateTransitionId(transitionId);
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Scan lifecycle state must be defined.");

        ScanId = scanId;
        Revision = revision;
        TransitionId = transitionId;
        State = state;
    }

    public Guid ScanId { get; }

    public long Revision { get; }

    public Guid TransitionId { get; }

    public ScanLifecycleState State { get; }

    internal static void ValidateScanId(Guid scanId)
    {
        if (scanId == Guid.Empty)
            throw new ArgumentException("Scan ID must be nonempty.", nameof(scanId));
    }

    internal static void ValidateRevision(long revision)
    {
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Checkpoint revision must be positive.");
    }

    internal static void ValidateTransitionId(Guid transitionId)
    {
        if (transitionId == Guid.Empty)
            throw new ArgumentException("Transition ID must be nonempty.", nameof(transitionId));
    }
}

public sealed record ScanCheckpoint
{
    public ScanCheckpoint(
        Guid scanId,
        long occurrenceId,
        ScanStage stage,
        long revision,
        Guid transitionId,
        StageOutcome outcome,
        ScanStagePrerequisite? prerequisite)
    {
        ScanLifecycleCheckpoint.ValidateScanId(scanId);
        if (occurrenceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(occurrenceId), occurrenceId, "Occurrence ID must be positive.");
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Scan stage must be defined.");
        ScanLifecycleCheckpoint.ValidateRevision(revision);
        ScanLifecycleCheckpoint.ValidateTransitionId(transitionId);
        ArgumentNullException.ThrowIfNull(outcome);

        ValidateStageShape(stage, outcome, prerequisite);

        ScanId = scanId;
        OccurrenceId = occurrenceId;
        Stage = stage;
        Revision = revision;
        TransitionId = transitionId;
        Outcome = outcome;
        Prerequisite = prerequisite;
    }

    public Guid ScanId { get; }

    public long OccurrenceId { get; }

    public ScanStage Stage { get; }

    public long Revision { get; }

    public Guid TransitionId { get; }

    public StageOutcome Outcome { get; }

    public ScanStagePrerequisite? Prerequisite { get; }

    private static void ValidateStageShape(
        ScanStage stage,
        StageOutcome outcome,
        ScanStagePrerequisite? prerequisite)
    {
        if (stage == ScanStage.Enumeration)
        {
            if (!ScanTransitionPolicy.IsTerminal(outcome.State))
                throw new ArgumentException("An Enumeration checkpoint must be terminal.", nameof(outcome));
            if (prerequisite is not null)
                throw new ArgumentException("An Enumeration checkpoint cannot have a prerequisite.", nameof(prerequisite));
            return;
        }

        if (prerequisite is null
            || prerequisite.Stage != ScanStage.Enumeration
            || !ScanTransitionPolicy.IsTerminal(prerequisite.Outcome.State))
        {
            throw new ArgumentException(
                "An Exact checkpoint requires a terminal Enumeration prerequisite.",
                nameof(prerequisite));
        }

        var enumerationSucceeded = prerequisite.Outcome.State == ScanStageState.Succeeded;
        if (enumerationSucceeded && outcome.State == ScanStageState.NotApplicable)
        {
            throw new ArgumentException(
                "Exact cannot be NotApplicable after successful Enumeration.",
                nameof(outcome));
        }

        if (!enumerationSucceeded
            && (outcome.State != ScanStageState.NotApplicable
                || outcome.Reason != prerequisite.Outcome.Reason))
        {
            throw new ArgumentException(
                "Exact must be NotApplicable with the prerequisite reason after unsuccessful Enumeration.",
                nameof(outcome));
        }
    }
}

public sealed record ScanCheckpointProjection
{
    public ScanCheckpointProjection(
        ScanCheckpoint stored,
        StageOutcome outcome,
        bool recoveredFromInterruption)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(outcome);

        if (recoveredFromInterruption)
        {
            if (stored.Outcome.State != ScanStageState.Running
                || outcome != new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure))
            {
                throw new ArgumentException(
                    "An interruption projection must replace a stored Running outcome with FailedRetryable(ReadFailure).",
                    nameof(recoveredFromInterruption));
            }
        }
        else if (outcome != stored.Outcome)
        {
            throw new ArgumentException(
                "A non-recovered projection must preserve the stored outcome.",
                nameof(outcome));
        }

        Stored = stored;
        Outcome = outcome;
        RecoveredFromInterruption = recoveredFromInterruption;
    }

    public ScanCheckpoint Stored { get; }

    public StageOutcome Outcome { get; }

    public bool RecoveredFromInterruption { get; }
}

public sealed record ScanAccounting
{
    public ScanAccounting(
        long discovered,
        long analyzed,
        long unsupported,
        long failed,
        long canceled,
        long outstanding)
    {
        ValidateCount(discovered, nameof(discovered));
        ValidateCount(analyzed, nameof(analyzed));
        ValidateCount(unsupported, nameof(unsupported));
        ValidateCount(failed, nameof(failed));
        ValidateCount(canceled, nameof(canceled));
        ValidateCount(outstanding, nameof(outstanding));

        long categorized;
        try
        {
            categorized = checked(analyzed + unsupported + failed + canceled + outstanding);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Accounting totals must not overflow.", nameof(discovered), exception);
        }

        if (discovered != categorized)
            throw new ArgumentException("Discovered must equal all categorized occurrence counts.", nameof(discovered));

        Discovered = discovered;
        Analyzed = analyzed;
        Unsupported = unsupported;
        Failed = failed;
        Canceled = canceled;
        Outstanding = outstanding;
    }

    public long Discovered { get; }

    public long Analyzed { get; }

    public long Unsupported { get; }

    public long Failed { get; }

    public long Canceled { get; }

    public long Outstanding { get; }

    private static void ValidateCount(long count, string name)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(name, count, "Accounting counts cannot be negative.");
    }
}

public static class ScanTransitionPolicy
{
    public static bool CanTransition(ScanLifecycleState current, ScanLifecycleState next)
    {
        Validate(current, nameof(current));
        Validate(next, nameof(next));

        return current switch
        {
            ScanLifecycleState.Preflight => next is ScanLifecycleState.Enumerating
                or ScanLifecycleState.Incomplete
                or ScanLifecycleState.Faulted,
            ScanLifecycleState.Enumerating => next is ScanLifecycleState.Exact
                or ScanLifecycleState.Pausing
                or ScanLifecycleState.Incomplete
                or ScanLifecycleState.Faulted,
            ScanLifecycleState.Exact => next is ScanLifecycleState.Complete
                or ScanLifecycleState.Pausing
                or ScanLifecycleState.Incomplete
                or ScanLifecycleState.Faulted,
            ScanLifecycleState.Pausing => next is ScanLifecycleState.Paused
                or ScanLifecycleState.Incomplete
                or ScanLifecycleState.Faulted,
            ScanLifecycleState.Paused => next is ScanLifecycleState.Enumerating
                or ScanLifecycleState.Exact
                or ScanLifecycleState.Incomplete
                or ScanLifecycleState.Faulted,
            ScanLifecycleState.Complete
                or ScanLifecycleState.Incomplete
                or ScanLifecycleState.Faulted => false,
            _ => false,
        };
    }

    public static bool CanTransition(ScanStage stage, ScanStageState current, ScanStageState next)
    {
        Validate(stage, nameof(stage));
        Validate(current, nameof(current));
        Validate(next, nameof(next));

        return stage == ScanStage.Exact
            && current == ScanStageState.Running
            && next is ScanStageState.Succeeded
                or ScanStageState.Unsupported
                or ScanStageState.FailedRetryable
                or ScanStageState.FailedTerminal
                or ScanStageState.Canceled;
    }

    public static bool IsTerminal(ScanStageState state)
    {
        Validate(state, nameof(state));
        return state is not ScanStageState.Pending and not ScanStageState.Running;
    }

    public static bool IsTerminal(ScanLifecycleState state)
    {
        Validate(state, nameof(state));
        return state is ScanLifecycleState.Complete
            or ScanLifecycleState.Incomplete
            or ScanLifecycleState.Faulted;
    }

    public static bool CanComplete(
        StageOutcome enumeration,
        IReadOnlyCollection<ScanCheckpoint> requiredStages,
        ScanAccounting stored)
    {
        ArgumentNullException.ThrowIfNull(enumeration);
        ArgumentNullException.ThrowIfNull(requiredStages);
        ArgumentNullException.ThrowIfNull(stored);

        return enumeration.State == ScanStageState.Succeeded
            && TryRecomputeAccounting(requiredStages, out var recomputed)
            && requiredStages.All(checkpoint => IsTerminal(checkpoint.Outcome.State))
            && recomputed.Outstanding == 0
            && recomputed == stored;
    }

    public static bool TryRecomputeAccounting(
        IReadOnlyCollection<ScanCheckpoint> stages,
        out ScanAccounting accounting)
    {
        ArgumentNullException.ThrowIfNull(stages);
        accounting = null!;

        if (stages.Count == 0
            || stages.Count % 2 != 0
            || stages.Any(stage => stage is null))
            return false;

        var scanId = stages.First().ScanId;
        if (stages.Any(stage => stage.ScanId != scanId))
            return false;

        long analyzed = 0;
        long unsupported = 0;
        long failed = 0;
        long canceled = 0;
        long outstanding = 0;
        var occurrences = stages.GroupBy(stage => stage.OccurrenceId).ToArray();
        foreach (var occurrence in occurrences)
        {
            var rows = occurrence.ToArray();
            if (rows.Length != 2)
                return false;
            var enumerationRows = rows.Where(row => row.Stage == ScanStage.Enumeration).ToArray();
            var exactRows = rows.Where(row => row.Stage == ScanStage.Exact).ToArray();
            if (enumerationRows.Length != 1 || exactRows.Length != 1)
                return false;
            var enumeration = enumerationRows[0];
            var exact = exactRows[0];
            if (exact.Prerequisite is null
                || exact.Prerequisite.Stage != ScanStage.Enumeration
                || exact.Prerequisite.Outcome != enumeration.Outcome)
            {
                return false;
            }

            switch (exact.Outcome.State)
            {
                case ScanStageState.Pending:
                case ScanStageState.Running:
                    outstanding++;
                    break;
                case ScanStageState.Succeeded:
                    analyzed++;
                    break;
                case ScanStageState.Unsupported:
                    unsupported++;
                    break;
                case ScanStageState.FailedRetryable:
                case ScanStageState.FailedTerminal:
                    failed++;
                    break;
                case ScanStageState.Canceled:
                    canceled++;
                    break;
                case ScanStageState.NotApplicable:
                    ClassifyNotApplicable(
                        enumeration.Outcome.State,
                        ref unsupported,
                        ref failed,
                        ref canceled);
                    break;
                default:
                    return false;
            }
        }

        accounting = new ScanAccounting(
            occurrences.LongLength,
            analyzed,
            unsupported,
            failed,
            canceled,
            outstanding);
        return true;
    }

    private static void ClassifyNotApplicable(
        ScanStageState prerequisite,
        ref long unsupported,
        ref long failed,
        ref long canceled)
    {
        switch (prerequisite)
        {
            case ScanStageState.Unsupported:
                unsupported++;
                break;
            case ScanStageState.Canceled:
                canceled++;
                break;
            default:
                failed++;
                break;
        }
    }

    private static void Validate<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(name, value, $"{typeof(T).Name} must be defined.");
    }
}
