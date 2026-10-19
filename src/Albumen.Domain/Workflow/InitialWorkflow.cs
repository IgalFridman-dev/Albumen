using System.Collections.Immutable;

namespace Albumen.Domain.Workflow;

public static class InitialWorkflow
{
    public static ImmutableArray<WorkflowPhaseState> Create() =>
    [
        new(WorkflowPhase.ChooseScope, "Choose folder", PhaseAvailability.Current),
        new(WorkflowPhase.ConfirmRules, "Scan options", PhaseAvailability.Unavailable),
        new(WorkflowPhase.Scan, "Scan", PhaseAvailability.Unavailable),
        new(WorkflowPhase.Review, "Review", PhaseAvailability.Unavailable),
        new(WorkflowPhase.ApproveCleanup, "Clean up", PhaseAvailability.Unavailable),
    ];
}
