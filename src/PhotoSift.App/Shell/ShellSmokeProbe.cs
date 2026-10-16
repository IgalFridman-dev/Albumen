using PhotoSift.Domain.Workflow;
using PhotoSift.Application.Shell;

namespace PhotoSift.App.Shell;

public static class ShellSmokeProbe
{
    private static readonly WorkflowPhase[] ExpectedPhases =
    [
        WorkflowPhase.ChooseScope,
        WorkflowPhase.ConfirmRules,
        WorkflowPhase.Scan,
        WorkflowPhase.Review,
        WorkflowPhase.ApproveCleanup,
    ];

    public static string Validate(object? dataContext)
    {
        if (dataContext is not ShellViewModel viewModel)
        {
            throw new InvalidOperationException("MainWindow must use ShellViewModel.");
        }

        var snapshot = viewModel.CurrentSnapshot;
        if (snapshot.ScopeState is not ScopeSessionState.Cold)
        {
            throw new InvalidOperationException("Shell must start with a Cold Scope.");
        }

        if (snapshot.Revision is not 0)
        {
            throw new InvalidOperationException("Shell must start at Scope revision zero.");
        }

        if (snapshot.Selection is not null)
        {
            throw new InvalidOperationException("Shell must start without a Scope selection.");
        }

        if (!viewModel.Phases.Select(phase => phase.Phase).SequenceEqual(ExpectedPhases))
        {
            throw new InvalidOperationException("Shell must expose the five ordered workflow phases.");
        }

        var currentPhases = viewModel.Phases
            .Where(phase => phase.Availability is PhaseAvailability.Current)
            .Select(phase => phase.Phase)
            .ToArray();
        if (currentPhases is not [WorkflowPhase.ChooseScope])
        {
            throw new InvalidOperationException("Only Choose Scope may be current initially.");
        }

        if (viewModel.Phases.Skip(1).Any(phase => phase.IsAvailable))
        {
            throw new InvalidOperationException("Every later phase must be unavailable initially.");
        }

        return "PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true";
    }
}
