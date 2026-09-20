namespace PhotoSift.Domain.Workflow;

public enum WorkflowPhase
{
    ChooseScope,
    ConfirmRules,
    Scan,
    Review,
    ApproveCleanup,
}
