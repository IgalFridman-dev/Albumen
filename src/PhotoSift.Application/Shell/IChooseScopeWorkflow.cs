namespace PhotoSift.Application.Shell;

public interface IChooseScopeWorkflow
{
    event EventHandler<ShellSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    ShellSnapshot Current => throw new NotSupportedException(
        "This temporary legacy workflow does not expose revisioned Scope state.");

    Task HandleAsync(ChooseScopeCommand command, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This temporary legacy workflow does not handle revisioned Scope commands."));

    Task HandleAsync(
        ChangeScopeRecursionCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This temporary legacy workflow does not handle revisioned Scope commands."));

    Task HandleAsync(
        RetryScopePreflightCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "This temporary legacy workflow does not handle revisioned Scope commands."));

    // Temporary compatibility for the pre-E1-S2 WPF consumer. Removed with its migration.
    Task<ShellSnapshot> OpenAsync(
        OpenChooseScopeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }
}
