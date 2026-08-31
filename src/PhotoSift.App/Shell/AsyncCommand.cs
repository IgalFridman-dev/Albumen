using System.Windows.Input;

namespace PhotoSift.App.Shell;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private bool isExecuting;

    public AsyncCommand(Func<Task> execute)
        : this(_ => execute())
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public AsyncCommand(Func<object?, Task> execute)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isExecuting;

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync() => ExecuteAsync(parameter: null);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute(parameter);
        }
        finally
        {
            isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
