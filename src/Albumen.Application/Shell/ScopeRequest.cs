namespace Albumen.Application.Shell;

// Mutable lifecycle fields belong exclusively to the workflow inbox. External
// code receives only Token; completion of work is distinct from cancellation.
internal sealed class ScopeRequest(CancellationToken callerCancellation) : IDisposable
{
    private readonly CancellationTokenSource source = new();
    private CancellationTokenRegistration callerRegistration;
    private Task cancellation = Task.CompletedTask;
    private bool cancellationRequested;
    private bool workFinished;

    public CancellationToken Token => source.Token;

    public CancellationToken CallerCancellation { get; } = callerCancellation;

    public void RegisterCallerCancellation(System.Action requestCancellation) =>
        callerRegistration = CallerCancellation.UnsafeRegister(
            _ => requestCancellation(), null);

    public void RequestCancellation()
    {
        if (workFinished || cancellationRequested)
        {
            return;
        }

        cancellationRequested = true;
        // Marks Token immediately, but runs callbacks asynchronously. The owner
        // records and later observes that task; it never waits here.
        cancellation = source.CancelAsync();
    }

    public Task FinishWork()
    {
        workFinished = true;
        callerRegistration.Unregister();
        return cancellation;
    }

    // Called by the owner only after FinishWork's cancellation task completes.
    public void Dispose() => source.Dispose();
}
