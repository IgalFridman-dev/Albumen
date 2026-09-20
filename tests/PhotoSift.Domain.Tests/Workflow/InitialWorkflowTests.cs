using System.Collections.Immutable;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Domain.Tests.Workflow;

public sealed class InitialWorkflowTests
{
    [Fact]
    public void CreateReturnsTheFiveProductPhasesInOrder()
    {
        var phases = InitialWorkflow.Create();

        Assert.IsType<ImmutableArray<WorkflowPhaseState>>(phases);
        Assert.Equal(
            [
                WorkflowPhase.ChooseScope,
                WorkflowPhase.ConfirmRules,
                WorkflowPhase.Scan,
                WorkflowPhase.Review,
                WorkflowPhase.ApproveCleanup,
            ],
            phases.Select(phase => phase.Phase));
        Assert.Equal(
            [
                "Choose Scope",
                "Confirm Rules",
                "Scan",
                "Review",
                "Approve Cleanup",
            ],
            phases.Select(phase => phase.DisplayName));
    }

    [Fact]
    public void CreateMakesOnlyChooseScopeAvailableInitially()
    {
        var phases = InitialWorkflow.Create();

        Assert.Equal(PhaseAvailability.Current, phases[0].Availability);
        Assert.All(
            phases.Skip(1),
            phase => Assert.Equal(PhaseAvailability.Unavailable, phase.Availability));
    }
}
