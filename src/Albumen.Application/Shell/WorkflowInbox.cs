using System.Collections.Concurrent;

namespace Albumen.Application.Shell;

// Only one turn is scheduled at a time. In particular, an observer submitting a
// command during publication cannot start a nested turn through a modal UI pump.
internal sealed class WorkflowInbox(IWorkflowScheduler scheduler)
{
    private readonly ConcurrentQueue<System.Action> pending = new();
    private int scheduled;

    public Task<T> InvokeAsync<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Enqueue(() =>
        {
            try
            {
                completion.SetResult(callback());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        Schedule();
        return completion.Task;
    }

    public Task InvokeAsync(System.Action callback) => InvokeAsync(() =>
    {
        callback();
        return true;
    });

    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref scheduled, 1, 0) == 0)
        {
            scheduler.Post(RunOne);
        }
    }

    private void RunOne()
    {
        try
        {
            if (pending.TryDequeue(out var callback))
            {
                callback();
            }
        }
        finally
        {
            Volatile.Write(ref scheduled, 0);
            if (!pending.IsEmpty)
            {
                Schedule();
            }
        }
    }
}
