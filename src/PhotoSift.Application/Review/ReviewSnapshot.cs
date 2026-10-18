using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Review;

// Selected is the mark held in this group. SelectedInGroup names the other group that
// holds the mark for the same occurrence, so every set shows the one global selection.
public sealed record ReviewMemberSnapshot(ScanFileOccurrence Occurrence, ReviewEligibilityState Eligibility,
    ReviewUnavailableReason Reason, bool Selected, Guid? SelectedInGroup = null);
public sealed record ReviewGroupSnapshot(CandidateGroup Candidate, ImmutableArray<ReviewMemberSnapshot> Members,
    ImmutableHashSet<long> SelectedOccurrenceIds, SelectionRecovery Recovery, bool Sealed, OverrideEvidenceId? OverrideEvidenceId);
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
public sealed record ReviewOpenRequest(ShellSnapshot Scope, ExactRuleConfirmation Rules, ScanSnapshot Scan);
public enum ReviewCommandOutcome
{
    Committed, Neutral, OverrideRequired, Stale, Sealed, Invalid,
    PersistenceFailed, EvidenceUnavailable, Approved, Faulted,
}
public sealed record ReviewCommandResult(ReviewCommandOutcome Outcome, ReviewSnapshot Snapshot,
    OverrideProposalToken? OverrideProposal = null, ApprovedActionRequest? ApprovedRequest = null);
