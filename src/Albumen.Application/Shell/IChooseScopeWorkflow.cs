namespace Albumen.Application.Shell;

public interface IChooseScopeWorkflow
{
    event EventHandler<ShellSnapshot>? SnapshotChanged;

    ShellSnapshot Current { get; }

    Task HandleAsync(ChooseScopeCommand command, CancellationToken cancellationToken);

    Task HandleAsync(
        ChangeScopeRecursionCommand command,
        CancellationToken cancellationToken);

    Task HandleAsync(
        RetryScopePreflightCommand command,
        CancellationToken cancellationToken);
}
