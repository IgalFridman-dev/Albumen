using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public interface IPhotoSiftWorkflow
{
    PhotoSiftSnapshot Current { get; }
    event EventHandler<PhotoSiftSnapshot>? SnapshotChanged;
    Task ContinueAsync();
    Task ConfirmExactRulesAsync();
    Task StartScanAsync();
    Task CancelScanAsync();
    Task OpenReviewAsync();
    Task<ReviewCommandResult> ChangeReviewSelectionAsync(
        SelectionCommand command, CancellationToken cancellationToken);
    Task<ReviewCommandResult> AffirmReviewOverrideAsync(
        OverrideAffirmationCommand command, CancellationToken cancellationToken);
    Task<ReviewCommandResult> ApproveReviewAsync(
        ApproveSelectionCommand command, CancellationToken cancellationToken);
    Task NavigateAsync(WorkflowPhase phase);
}
