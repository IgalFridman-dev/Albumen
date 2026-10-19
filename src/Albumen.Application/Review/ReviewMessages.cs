using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;

namespace Albumen.Application.Review;

public enum ReviewEligibilityState { PendingRevalidation, Eligible, Unavailable }
public enum ReviewUnavailableReason
{
    None, Missing, Changed, AccessDenied, SharingViolation, UnsafeLocation,
    UnknownSafety, ReadFailure, Canceled, Stale, StoreUnavailable, ProgrammingFault,
}

public sealed record ReviewRevalidationRequest(
    ReviewSessionPin Session,
    ScanRunContext Context,
    Guid GroupId,
    long GroupRevision,
    ScanFileOccurrence Occurrence,
    long Attempt);

public sealed record ReviewRevalidationEvidence(
    ReviewRevalidationRequest Request,
    ProtectedFileFacts Opened,
    ProtectedFileFacts Final,
    StreamDigest Digest);

public enum OverridePersistOutcome { Persisted, ExactRetry, Conflict, Unavailable }
public sealed record OverridePersistResult(OverridePersistOutcome Outcome);
public enum OverrideReadOutcome { Found, Missing, Corrupt, Unavailable }
public sealed record OverrideReadResult(OverrideReadOutcome Outcome, DeliberateOverrideEvidence? Evidence);

public readonly record struct ApprovedActionClaimId(Guid Value);
public sealed record ApprovedActionClaim(ApprovedActionClaimId ClaimId, ApprovedActionRequest Request);
public sealed record ActionProbeObservation(ScanFileOccurrence ApprovedOccurrence,
    ProtectedFileFacts Opened, ProtectedFileFacts Final, StreamDigest Digest);
public sealed record ActionGroupProbeResult(Guid GroupId, long GroupRevision,
    ImmutableArray<ActionProbeObservation> Observations, ImmutableArray<long> MissingOccurrenceIds);
public enum ApprovedActionClaimOutcome
{
    Claimed, Existing, Stale, Invalid, EvidenceUnavailable, UnsafeCurrentFacts, Faulted,
}
public sealed record ApprovedActionClaimResult(ApprovedActionClaimOutcome Outcome,
    ApprovedActionClaim? Claim, ActionSafetyReason Reason);
public sealed record ActionSafetyEnvelope(ApprovedActionClaim Claim, ApprovedActionItem Item,
    ActionSafetyInput Input, ActionSafetyDecision Decision,
    ImmutableArray<ActionProbeObservation> Observations,
    ImmutableArray<PriorActionOutcome> PrecedingOutcomes);
public sealed record ActionSafetyRefreshResult(ApprovedActionClaimOutcome Outcome,
    ActionSafetyEnvelope? Envelope, ActionSafetyReason Reason);

internal sealed record ActionClaimAttempt(Guid AttemptId, ApprovedActionRequest Request,
    ScanRunContext Context, ImmutableArray<CandidateGroup> Groups,
    ImmutableArray<OverrideReadResult> Reads,
    TaskCompletionSource<ApprovedActionClaimResult> Completion);
internal sealed record ActionSafetyRefreshAttempt(Guid AttemptId, ApprovedActionClaim Claim,
    ApprovedActionItem Item, ScanRunContext Context, ImmutableArray<CandidateGroup> Groups,
    ImmutableArray<PriorActionOutcome> PrecedingOutcomes,
    ImmutableArray<DeliberateOverrideEvidence> ExpectedEvidence,
    TaskCompletionSource Settled);
