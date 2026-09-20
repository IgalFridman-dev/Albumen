using System.Collections.Concurrent;
using PhotoSift.Application.Shell;

namespace PhotoSift.Application.Action;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The async-only semaphore creates no wait handle; admitted acknowledgments must keep draining.")]
public sealed class ActionOwnerInbox
{
    private readonly IWorkflowScheduler scheduler;
    private readonly SemaphoreSlim resultSlots;
    private readonly ConcurrentQueue<System.Action> pending = new();
    private int scheduled;
    internal System.Action? DeliverAfterTurn { get; set; }

    public ActionOwnerInbox(IWorkflowScheduler scheduler, int resultCapacity = 16)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultCapacity);
        resultSlots = new(resultCapacity, resultCapacity);
    }
    public Task<T> CommandAsync<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Enqueue(() =>
        {
            try
            {
                var result = callback();
                DeliverAfterTurn?.Invoke();
                completion.TrySetResult(result);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        Schedule();
        return completion.Task;
    }
    public async Task<T> ResultAsync<T>(Func<T> callback)
    {
        await resultSlots.WaitAsync().ConfigureAwait(false);
        try { return await CommandAsync(callback).ConfigureAwait(false); }
        finally { resultSlots.Release(); }
    }
    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref scheduled, 1, 0) == 0) scheduler.Post(RunOne);
    }
    private void RunOne()
    {
        try { if (pending.TryDequeue(out var callback)) callback(); }
        finally
        {
            Volatile.Write(ref scheduled, 0);
            if (!pending.IsEmpty) Schedule();
        }
    }
}
