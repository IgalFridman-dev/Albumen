namespace PhotoSift.Domain.Workflow;

public sealed record WorkflowPhaseState(
    WorkflowPhase Phase,
    string DisplayName,
    PhaseAvailability Availability);
