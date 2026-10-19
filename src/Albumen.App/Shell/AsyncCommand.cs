using System.Windows.Input;

namespace Albumen.App.Shell;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly bool allowConcurrentExecutions;
    private readonly Func<bool>? canExecute;
    private int executionCount;

    public AsyncCommand(
        Func<Task> execute,
        bool allowConcurrentExecutions = false,
        Func<bool>? canExecute = null)
        : this(_ => execute(), allowConcurrentExecutions, canExecute)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public AsyncCommand(
        Func<object?, Task> execute,
        bool allowConcurrentExecutions = false,
        Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.allowConcurrentExecutions = allowConcurrentExecutions;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    internal void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) =>
        (canExecute?.Invoke() ?? true)
        && (allowConcurrentExecutions || Volatile.Read(ref executionCount) == 0);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync() => ExecuteAsync(parameter: null);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!(canExecute?.Invoke() ?? true) || !TryBeginExecution())
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
            NotifyCanExecuteChanged();
        }
    }
}
