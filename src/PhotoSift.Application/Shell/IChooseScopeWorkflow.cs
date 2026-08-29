namespace PhotoSift.Application.Shell;

public interface IChooseScopeWorkflow
{
    Task<ShellSnapshot> OpenAsync(
        OpenChooseScopeCommand command,
        CancellationToken cancellationToken);
}
