using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public interface IPhotoSiftWorkflow
{
    PhotoSiftSnapshot Current { get; }
    event EventHandler<PhotoSiftSnapshot>? SnapshotChanged;
    Task ContinueAsync();
    Task SetScanOptionsAsync(ScanOptions options);
    Task ConfirmExactRulesAsync();
    Task StartScanAsync();
    Task CancelScanAsync();
    Task OpenReviewAsync();
    // PM-013: reopen the review of the same scan after its cleanup settled.
    Task ContinueReviewAsync();
    Task<ReviewCommandResult> ChangeReviewSelectionAsync(
        SelectionCommand command, CancellationToken cancellationToken);
    Task<ReviewCommandResult> AffirmReviewOverrideAsync(
        OverrideAffirmationCommand command, CancellationToken cancellationToken);
    Task<ReviewCommandResult> ApproveReviewAsync(
        ApproveSelectionCommand command, CancellationToken cancellationToken);
    Task RevalidateUnknownAsync(Guid requestId, CancellationToken cancellationToken);
    Task NavigateAsync(WorkflowPhase phase);
}
