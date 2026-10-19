using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Application.Shell;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;

namespace Albumen.Application.Review;

// Selected is the mark held in this group. SelectedInGroup names the other group that
// holds the mark for the same occurrence, so every set shows the one global selection.
public sealed record ReviewMemberSnapshot(ScanFileOccurrence Occurrence, ReviewEligibilityState Eligibility,
    ReviewUnavailableReason Reason, bool Selected, Guid? SelectedInGroup = null);
public sealed record ReviewGroupSnapshot(CandidateGroup Candidate, ImmutableArray<ReviewMemberSnapshot> Members,
    ImmutableHashSet<long> SelectedOccurrenceIds, SelectionRecovery Recovery, bool Sealed, OverrideEvidenceId? OverrideEvidenceId)
{
    // Closed after a cleanup: the set lost members to the Recycle Bin or to an
    // uncertain outcome and takes no marks until the next scan. Candidate keeps the
    // scan's members; Members holds only the photos still under review.
    public bool Closed { get; init; }
}
public sealed record ReviewUnresolvedSummary(long IncompleteEnumeration, long FailedScanRows, long CanceledScanRows,
    long UnprocessedScanRows, long PendingReviewMembers, long UnavailableReviewMembers, long UnselectedSurvivorMembers)
{
    public long Total => checked(IncompleteEnumeration + FailedScanRows + CanceledScanRows + UnprocessedScanRows
        + PendingReviewMembers + UnavailableReviewMembers);
}
public enum ReviewStatus { Closed, Revalidating, Ready, Faulted }
public sealed record ReviewSnapshot(ReviewSessionPin? Pin, ReviewStatus Status, ScanStatus Coverage,
    long SelectionRevision, ImmutableArray<ReviewGroupSnapshot> Groups, ReviewUnresolvedSummary Unresolved,
    bool Sealed, ApprovedActionRequest? ApprovedRequest, ReviewUnavailableReason FaultReason)
{
    public static ReviewSnapshot Closed { get; } = new(null, ReviewStatus.Closed, ScanStatus.Idle, 0, [],
        new(0, 0, 0, 0, 0, 0, 0), false, null, ReviewUnavailableReason.None);
}
// A continuation reopens the review of the same scan after its cleanup settled:
// ContinuesRequestId names the finished cleanup, ExcludedOccurrenceIds the photos
// it moved to the Recycle Bin (they leave every set) and ClosedGroupIds the sets
// with an uncertain outcome (closed until the next scan).
public sealed record ReviewOpenRequest(ShellSnapshot Scope, ExactRuleConfirmation Rules, ScanSnapshot Scan)
{
    public ImmutableHashSet<long> ExcludedOccurrenceIds { get; init; } = ImmutableHashSet<long>.Empty;
    public ImmutableHashSet<Guid> ClosedGroupIds { get; init; } = ImmutableHashSet<Guid>.Empty;
    public Guid? ContinuesRequestId { get; init; }
}
public enum ReviewCommandOutcome
{
    Committed, Neutral, OverrideRequired, Stale, Sealed, Invalid,
    PersistenceFailed, EvidenceUnavailable, Approved, Faulted,
}
public sealed record ReviewCommandResult(ReviewCommandOutcome Outcome, ReviewSnapshot Snapshot,
    OverrideProposalToken? OverrideProposal = null, ApprovedActionRequest? ApprovedRequest = null);
