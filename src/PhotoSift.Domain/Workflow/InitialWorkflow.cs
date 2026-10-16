using System.Collections.Immutable;

namespace PhotoSift.Domain.Workflow;

public static class InitialWorkflow
{
    public static ImmutableArray<WorkflowPhaseState> Create() =>
    [
        new(WorkflowPhase.ChooseScope, "Choose Scope", PhaseAvailability.Current),
        new(WorkflowPhase.ConfirmRules, "Confirm Rules", PhaseAvailability.Unavailable),
        new(WorkflowPhase.Scan, "Scan", PhaseAvailability.Unavailable),
        new(WorkflowPhase.Review, "Review", PhaseAvailability.Unavailable),
        new(WorkflowPhase.ApproveCleanup, "Approve Cleanup", PhaseAvailability.Unavailable),
    ];
}
