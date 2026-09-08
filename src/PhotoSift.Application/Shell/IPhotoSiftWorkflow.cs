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
    Task NavigateAsync(WorkflowPhase phase);
}
