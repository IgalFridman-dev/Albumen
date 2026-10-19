using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;

namespace Albumen.Application.Review;

public interface IReviewCoordinator : IAsyncDisposable
{
    ReviewSnapshot Current { get; }
    event EventHandler<ReviewSnapshot>? SnapshotChanged;
    Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken);
    Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken cancellationToken);
    Task<ReviewCommandResult> AffirmOverrideAsync(OverrideAffirmationCommand command, CancellationToken cancellationToken);
    Task<ReviewCommandResult> ApproveAsync(ApproveSelectionCommand command, CancellationToken cancellationToken);
    Task ReportObserverFaultAsync(ReviewSessionPin session);
    Task InvalidateAsync(ScanPin? currentScan);
}

public interface IReviewResultSink
{
    // The returned acknowledgment settles after owner delivery, independently of
    // worker cancellation. The worker retains its lease until this value settles.
    ValueTask<bool> CommitRevalidationAsync(ReviewRevalidationEvidence evidence);
    ValueTask<bool> FailRevalidationAsync(ReviewRevalidationRequest request, ReviewUnavailableReason reason);
}

public interface IReviewRun : IAsyncDisposable
{
    Task Completion { get; }
    Task CancelAsync();
}

public interface IReviewRevalidationExecutor
{
    // The Review owner admits terminal Complete/Incomplete Scan groups before
    // starting a run; this transport does not own Scan status or member eligibility.
    IReviewRun Start(ReviewSessionPin session, ScanRunContext context,
        ImmutableArray<CandidateGroup> groups, IReviewResultSink sink);
}

public interface IOverrideEvidenceStore
{
    ValueTask<OverridePersistResult> PersistAsync(DeliberateOverrideEvidence evidence, CancellationToken cancellationToken);
    ValueTask<OverrideReadResult> ReadAsync(OverrideEvidenceId evidenceId, CancellationToken cancellationToken);
}

public interface IApprovedActionAuthority
{
    Task<ApprovedActionClaimResult> ClaimAsync(ApprovedActionRequest request,
        ImmutableArray<OverrideReadResult> overrideReads, CancellationToken cancellationToken);
    Task<ActionSafetyRefreshResult> RefreshSafetyAsync(ApprovedActionClaim claim,
        ApprovedActionItem item, ImmutableArray<PriorActionOutcome> precedingOutcomes,
        CancellationToken cancellationToken);
}
