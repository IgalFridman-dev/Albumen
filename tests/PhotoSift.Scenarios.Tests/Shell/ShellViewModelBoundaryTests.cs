using System.Collections.Immutable;
using PhotoSift.App.Shell;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class ShellViewModelBoundaryTests
{
    [Fact]
    public async Task ChooseScopeActionUsesApplicationPortAndConsumesItsSnapshot()
    {
        var returnedPhases = ImmutableArray.Create(
            new WorkflowPhaseState(
                WorkflowPhase.ChooseScope,
                "Choose Scope",
                PhaseAvailability.Unavailable),
            new WorkflowPhaseState(
                WorkflowPhase.ConfirmRules,
                "Confirm Rules",
                PhaseAvailability.Current),
            new WorkflowPhaseState(
                WorkflowPhase.Scan,
                "Scan",
                PhaseAvailability.Unavailable),
            new WorkflowPhaseState(
                WorkflowPhase.Review,
                "Review",
                PhaseAvailability.Unavailable),
            new WorkflowPhaseState(
                WorkflowPhase.ApproveCleanup,
                "Approve Cleanup",
                PhaseAvailability.Unavailable));
        var workflow = new RecordingChooseScopeWorkflow(
            new ShellSnapshot(returnedPhases, "Instruction returned by Application."));
        var viewModel = new ShellViewModel(workflow);

        await viewModel.OpenChooseScopeAsync(CancellationToken.None);

        Assert.Equal(1, workflow.CallCount);
        Assert.IsType<OpenChooseScopeCommand>(workflow.ReceivedCommand);
        Assert.Equal("Instruction returned by Application.", viewModel.Instruction);
        Assert.Equal(
            returnedPhases.Select(phase => (phase.Phase, phase.DisplayName, phase.Availability)),
            viewModel.Phases.Select(phase => (phase.Phase, phase.DisplayName, phase.Availability)));
    }

    [Fact]
    public void SmokeProbeAcceptsOnlyTheExpectedInitialShellBoundary()
    {
        var viewModel = new ShellViewModel(new NativeOfflineShell());

        var marker = ShellSmokeProbe.Validate(viewModel);

        Assert.Equal(
            "PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true",
            marker);
    }

    private sealed class RecordingChooseScopeWorkflow(ShellSnapshot snapshot)
        : IChooseScopeWorkflow
    {
        public int CallCount { get; private set; }

        public OpenChooseScopeCommand? ReceivedCommand { get; private set; }

        public Task<ShellSnapshot> OpenAsync(
            OpenChooseScopeCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            ReceivedCommand = command;
            return Task.FromResult(snapshot);
        }
    }
}
