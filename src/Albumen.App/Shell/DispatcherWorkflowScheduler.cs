using System.Windows.Threading;
using Albumen.Application.Shell;

namespace Albumen.App.Shell;

internal sealed class DispatcherWorkflowScheduler(Dispatcher dispatcher) : IWorkflowScheduler
{
    public void Post(Action callback) => dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
}
