namespace Albumen.Domain.Workflow;

public enum WorkflowPhase
{
    ChooseScope,
    ConfirmRules,
    Scan,
    Review,
    ApproveCleanup,
}
