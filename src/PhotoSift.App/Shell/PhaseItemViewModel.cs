using PhotoSift.Domain.Workflow;

namespace PhotoSift.App.Shell;

public sealed record PhaseItemViewModel(
    WorkflowPhase Phase,
    string DisplayName,
    PhaseAvailability Availability)
{
    public bool IsAvailable => Availability is PhaseAvailability.Current or PhaseAvailability.Available;

    public bool IsCurrent => Availability is PhaseAvailability.Current;

    public int StepNumber => (int)Phase + 1;

    public string AvailabilityLabel => Availability switch
    {
        PhaseAvailability.Current => "Current step",
        PhaseAvailability.Available => "Available",
        PhaseAvailability.Unavailable => "Unavailable",
        _ => throw new InvalidOperationException($"Unknown availability: {Availability}"),
    };
}

