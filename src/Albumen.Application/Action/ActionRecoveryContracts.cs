using System.Collections.Immutable;
using Albumen.Domain.Scan;

namespace Albumen.Application.Action;

public enum ActionRevalidationKind { ConfirmedPresent, StillUnknown }
public enum ActionRevalidationReason
{
    OriginalFileConfirmed, OriginalPathMissing, OriginalPathChanged, AccessUnavailable,
    LocationUnavailable, TimedOut, Failed,
}
public sealed record ActionRevalidationObservation(bool PathExists, PhysicalFileKey? ObservedKey,
    long? ObservedLength, string? ObservedSha256);
public sealed record ActionRevalidationFact(Guid RevalidationId, Guid ActionItemId, DateTimeOffset ObservedUtc,
    ActionRevalidationKind Kind, ActionRevalidationReason Reason, ActionRevalidationObservation Observation);
public sealed record PersistedActionItem(Guid RequestId, ActionRequestedFact Requested, ActionExecutingFact? Executing,
    ActionTerminalFact? Terminal, ImmutableArray<ActionRevalidationFact> Revalidations);
public enum ActionRecordLoadOutcome { Loaded, Unavailable }
public sealed record ActionRecordLoadResult(ActionRecordLoadOutcome Outcome, ImmutableArray<PersistedActionItem> Items);
public enum ActionRevalidationWriteOutcome { Persisted, ExactRetry, Conflict, Resolved, Unavailable }
public sealed record ActionRevalidationWriteResult(ActionRevalidationWriteOutcome Outcome);
