using System.Collections.Immutable;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Domain.Action;

public enum PriorActionDisposition
{
    Pending,
    Recycled,
    AffirmedNotRemoved,
    NotRequested,
    Unknown,
}

public sealed record PriorActionOutcome(
    Guid GroupId,
    long OccurrenceId,
    PriorActionDisposition Disposition);

public sealed record ActionSafetyInput(
    CandidateGroup ApprovedGroup,
    ImmutableArray<ScanFileOccurrence> CurrentOccurrences,
    ImmutableHashSet<long> FreshEligibleOccurrenceIds,
    ImmutableHashSet<long> RemainingApprovedOccurrenceIds,
    ImmutableHashSet<long> OverrideApprovedOccurrenceIds,
    bool OverrideEvidenceExact,
    bool HasDependentUnknown);

public enum ActionSafetyDecisionKind
{
    Allowed,
    StaleAuthority,
    UnsafeCurrentFacts,
    OverrideUnavailable,
}

public enum ActionSafetyReason
{
    None,
    StalePin,
    Missing,
    Changed,
    AccessDenied,
    SharingViolation,
    UnsafeLocation,
    UnknownSafety,
    Synchronized,
    Placeholder,
    ReadOnly,
    PermissionDenied,
    Unavailable,
    CannotRecycle,
    CallbackContradiction,
    InconsistentLinks,
    NoCurrentSurvivor,
    OverrideMissing,
    OverrideMismatch,
    DependentOutcomeUnknown,
    StoreUnavailable,
    JournalConflict,
    JournalUnavailable,
    Interrupted,
    ProgrammingFault,
}

public sealed record ActionSafetyDecision(
    ActionSafetyDecisionKind Kind,
    ActionSafetyReason Reason,
    SurvivorDecision Survivor);
