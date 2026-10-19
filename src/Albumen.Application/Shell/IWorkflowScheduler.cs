namespace Albumen.Application.Shell;

/// <summary>Schedules work without invoking it inline. UI hosts supply their UI scheduler.</summary>
public interface IWorkflowScheduler
{
    void Post(System.Action callback);
}
