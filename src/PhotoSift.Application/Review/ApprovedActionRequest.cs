using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

public sealed record ApprovalDisclosure(long SelectedOccurrenceCount, long DistinctPhysicalKeyCount,
    ScanStatus Coverage, ReviewUnresolvedSummary Unresolved, SelectionRecovery Recovery,
    string FutureDestination, string RevalidationNotice);
public sealed record ApprovedActionItem(Guid GroupId, long GroupRevision, OccurrenceToken Occurrence,
    string DiscoveredPath, string ResolvedPath, PhysicalFileKey PhysicalFileKey, ContentRevision Content,
    AllocationEvidence Allocation, OverrideEvidenceId? OverrideEvidenceId);
public sealed record ApprovedActionRequest(Guid RequestId, DateTimeOffset ApprovedUtc, Guid ReviewSessionId,
    long SelectionRevision, ScanPin Scan, ResolvedScope Scope, ImmutableArray<ApprovedActionItem> Items,
    ImmutableArray<long> SelectedOccurrenceIds, ApprovalDisclosure Disclosure);
