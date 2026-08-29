using PhotoSift.Domain.Workflow;

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

        return "PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true";
    }
}
