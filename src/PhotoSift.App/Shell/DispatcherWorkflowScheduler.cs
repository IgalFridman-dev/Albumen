using System.Windows.Threading;
using PhotoSift.Application.Shell;

namespace PhotoSift.App.Shell;

internal sealed class DispatcherWorkflowScheduler(Dispatcher dispatcher) : IWorkflowScheduler
{
    public void Post(Action callback) => dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
}
