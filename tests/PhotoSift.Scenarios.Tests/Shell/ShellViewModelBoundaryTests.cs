using PhotoSift.App.Shell;
using PhotoSift.App.Composition;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class ShellViewModelBoundaryTests
{
    [Fact]
    public void ConcreteCompositionStartsColdUnselectedLockedAndKeepsOfflineMarker()
    {
        var viewModel = CompositionRoot.CreateShellViewModel();

        var marker = ShellSmokeProbe.Validate(viewModel);

        Assert.Equal("Choose one local folder", viewModel.ScopeStatus);
        Assert.Equal(string.Empty, viewModel.SelectedPath);
        Assert.All(viewModel.Phases.Skip(1), phase => Assert.False(phase.IsAvailable));
        Assert.Equal(
            "PHOTOSIFT_SMOKE_OK phases=5 current=ChooseScope offline=true",
            marker);
    }

    [Fact]
    public void SmokeProbeRejectsANonzeroInitialScopeRevision()
    {
        var snapshot = ColdSnapshot() with { Revision = 1 };
        var viewModel = new ShellViewModel(new StaticChooseScopeWorkflow(snapshot));

        Assert.Throws<InvalidOperationException>(() => ShellSmokeProbe.Validate(viewModel));
    }

    [Fact]
    public void SmokeProbeRejectsAnInitialScopeSelection()
    {
        var snapshot = ColdSnapshot() with
        {
            Selection = new ScopeSelection(
                @"C:\Photos",
                ScopeRecursion.TopLevelOnly),
        };
        var viewModel = new ShellViewModel(new StaticChooseScopeWorkflow(snapshot));

        Assert.Throws<InvalidOperationException>(() => ShellSmokeProbe.Validate(viewModel));
    }

    [Fact]
    public void SmokeProbeRejectsANonColdInitialScopeState()
    {
        var snapshot = ColdSnapshot() with { ScopeState = ScopeSessionState.Checking };
        var viewModel = new ShellViewModel(new StaticChooseScopeWorkflow(snapshot));

        Assert.Throws<InvalidOperationException>(() => ShellSmokeProbe.Validate(viewModel));
    }

    private static ShellSnapshot ColdSnapshot() =>
        new(
            InitialWorkflow.Create(),
            "Choose one local folder to scan.",
            ScopeSessionState.Cold);

    private sealed class StaticChooseScopeWorkflow(ShellSnapshot current)
        : IChooseScopeWorkflow
    {
        public event EventHandler<ShellSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public ShellSnapshot Current { get; } = current;

        public Task HandleAsync(
            ChooseScopeCommand command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task HandleAsync(
            ChangeScopeRecursionCommand command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task HandleAsync(
            RetryScopePreflightCommand command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
