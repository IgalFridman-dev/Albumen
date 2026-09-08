using PhotoSift.Application.Shell;

namespace PhotoSift.Application.Scan;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The async-only semaphore never creates a wait handle; closing the owner must not strand admitted acknowledgments.")]
public sealed class ScanOwnerInbox
{
    private readonly SemaphoreSlim resultSlots;
    private readonly WorkflowInbox turns;

    public ScanOwnerInbox(IWorkflowScheduler scheduler, int resultCapacity = 16)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultCapacity);
        resultSlots = new SemaphoreSlim(resultCapacity, resultCapacity);
        turns = new WorkflowInbox(scheduler);
    }

    public Task<T> CommandAsync<T>(Func<T> callback) => turns.InvokeAsync(callback);

    public async Task<T> ResultAsync<T>(Func<T> callback)
    {
        // Capacity is acquired before entering the owner queue. Commands never
        // wait on this lane, including replacement and cancellation commands.
        await resultSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await turns.InvokeAsync(callback).ConfigureAwait(false);
        }
        finally
        {
            resultSlots.Release();
        }
    }
}
