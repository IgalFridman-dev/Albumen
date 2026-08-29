using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public sealed class NativeOfflineShell : IChooseScopeWorkflow
{
    public Task<ShellSnapshot> OpenAsync(
        OpenChooseScopeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            new ShellSnapshot(
                InitialWorkflow.Create(),
                "Choose one local folder to scan."));
    }
}
