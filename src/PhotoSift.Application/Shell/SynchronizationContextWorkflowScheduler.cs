namespace PhotoSift.Application.Shell;

// Platform-neutral default for non-WPF hosts. The WPF composition root supplies
// an explicit Dispatcher adapter instead of depending on an ambient context.
internal sealed class SynchronizationContextWorkflowScheduler : IWorkflowScheduler
{
    private readonly SynchronizationContext context =
        SynchronizationContext.Current ?? new SynchronizationContext();

    public void Post(System.Action callback) => context.Post(_ => callback(), null);
}
