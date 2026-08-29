using PhotoSift.Domain.Workflow;

namespace PhotoSift.App.Shell;

public sealed record PhaseItemViewModel(
    WorkflowPhase Phase,
    string DisplayName,
    PhaseAvailability Availability)
{
    public bool IsAvailable => Availability is PhaseAvailability.Current;

    public string AvailabilityLabel => Availability switch
    {
        PhaseAvailability.Current => "Available now",
        PhaseAvailability.Unavailable => "Unavailable",
        _ => throw new InvalidOperationException($"Unknown availability: {Availability}"),
    };
}
