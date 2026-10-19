using Albumen.Application.Review;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Workflow;

namespace Albumen.Application.Shell;

public interface IAlbumenWorkflow
{
    AlbumenSnapshot Current { get; }
    event EventHandler<AlbumenSnapshot>? SnapshotChanged;
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
