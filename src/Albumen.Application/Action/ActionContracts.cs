using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Albumen.Application.Review;
using Albumen.Domain.Action;
using Albumen.Domain.Review;

namespace Albumen.Application.Action;

public enum ActionStatus { Idle, Claiming, Running, Complete, Incomplete, Faulted }
public enum ActionItemState
{
    Waiting, NotRequested, Requested, InterruptedBeforeExecution,
    Executing, Recycled, NotRemoved, UnknownPendingRevalidation,
}
public sealed record ActionItemSnapshot(ApprovedActionItem Item, Guid? ActionItemId,
    ActionItemState State, ActionSafetyReason Reason);
public sealed record ActionSnapshot(Guid? RequestId, ActionStatus Status,
    ImmutableArray<ActionItemSnapshot> Items, long DurableRequestedCount, long DurableExecutingCount,
    long RecycledCount, long NotRemovedCount, long UnknownCount, ActionSafetyReason FaultReason)
{
    public static ActionSnapshot Idle { get; } = new(null, ActionStatus.Idle, [], 0, 0, 0, 0, 0, ActionSafetyReason.None);
}
public enum ActionCommandOutcome { Started, Existing, Rejected, Completed, Incomplete, Faulted }
public sealed record ActionCommandResult(ActionCommandOutcome Outcome, ActionSnapshot Snapshot);

public enum ActionJournalWriteOutcome { Persisted, ExactRetry, Conflict, InvalidPredecessor, Unavailable }
public sealed record ActionJournalWriteResult(ActionJournalWriteOutcome Outcome);
public sealed record ActionRequestedFact(Guid ActionItemId, DateTimeOffset RequestedUtc,
    ActionSafetyEnvelope Safety, string RequestPayloadSha256, DeliberateOverrideEvidence? OverrideEvidence);
public sealed record ActionExecutingFact(Guid ActionItemId, DateTimeOffset ExecutingUtc);
public enum ActionTerminalKind { Recycled, NotRemoved, UnknownPendingRevalidation }
public sealed record ActionTerminalFact(Guid ActionItemId, DateTimeOffset RecordedUtc,
    ActionTerminalKind Kind, ActionSafetyReason Reason, RecycleBinEvidence Evidence);

public enum RecycleBinOutcomeKind { Recycled, NotRemoved, UnknownPendingRevalidation }
public sealed record RecycleBinEvidence(int ValidPreDeleteCount, int UnexpectedPreDeleteCount,
    bool PermissionGranted, int? PerformOperationsHResult, bool? OperationsAborted,
    int MatchingPostDeleteCount, int UnexpectedPostDeleteCount, int? DeleteHResult,
    bool NewlyCreatedItemPresent, bool CallbackException, bool OriginalObjectProvenUnchanged)
{
    // Absent fields preserve the canonical eleven-field journal payload. Nullable
    // values distinguish unobserved facts from observed zero/success/false.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? PreparationHResult { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActionSafetyReason? ValidationReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool? SelectedHandleReleased { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? GetAbortedHResult { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool? RunnerInterrupted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? FinishOperationsHResult { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool? NamespaceHandoffOccurred { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool? PerformOperationsEntered { get; init; }
}
public sealed record RecycleBinRequest(Guid ActionItemId, ActionSafetyEnvelope Safety);
public sealed record RecycleBinResult(RecycleBinOutcomeKind Outcome, ActionSafetyReason Reason, RecycleBinEvidence Evidence);
