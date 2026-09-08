using System.Windows.Input;

namespace PhotoSift.App.Shell;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly bool allowConcurrentExecutions;
    private int executionCount;

    public AsyncCommand(
        Func<Task> execute,
        bool allowConcurrentExecutions = false)
        : this(_ => execute(), allowConcurrentExecutions)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public AsyncCommand(
        Func<object?, Task> execute,
        bool allowConcurrentExecutions = false)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.allowConcurrentExecutions = allowConcurrentExecutions;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        allowConcurrentExecutions || Volatile.Read(ref executionCount) == 0;

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync() => ExecuteAsync(parameter: null);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!TryBeginExecution())
        {
            return;
        }

        NotifyAvailabilityChanged();
        try
        {
            await execute(parameter);
        }
        finally
        {
            Interlocked.Decrement(ref executionCount);
            NotifyAvailabilityChanged();
        }
    }

    private bool TryBeginExecution()
    {
        if (allowConcurrentExecutions)
        {
            Interlocked.Increment(ref executionCount);
            return true;
        }

        return Interlocked.CompareExchange(ref executionCount, 1, 0) == 0;
    }

    private void NotifyAvailabilityChanged()
    {
        // Concurrent scope commands stay available throughout execution. Do not
        // notify WPF controls from arbitrary command callers when nothing changed.
        if (!allowConcurrentExecutions)
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
