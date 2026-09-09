using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Review;

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
