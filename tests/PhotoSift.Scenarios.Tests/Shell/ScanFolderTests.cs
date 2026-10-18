using System.Collections.Immutable;
using PhotoSift.App.Shell;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

// PM-009 PR 3c: the folder and the scan options share one screen with one Scan button.
public sealed class ScanFolderTests
{
    [Fact]
    public async Task OneScanButtonConfirmsTheShownOptionsAndStartsTheScan()
    {
        var scope = new ExactShellFixture.StaticScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new RecordingScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler);
        var viewModel = new ShellViewModel(scope, workflow);

        Assert.True(viewModel.IsChooseFolderPhase);
        Assert.True(viewModel.CanChangeScanOptions);
        Assert.Equal("Scan 2 photos", viewModel.ScanFolderText);
        await scheduler.Finish(viewModel.ChangeNearDuplicateModeCommand.ExecuteAsync(false));
        Assert.True(viewModel.IsChooseScopePhase);
        Assert.Empty(scan.Starts);

        Assert.True(viewModel.CanScanFolder);
        await scheduler.Finish(viewModel.ScanFolderCommand.ExecuteAsync());

        var started = Assert.Single(scan.Starts);
        Assert.Equal(NearDuplicateMode.Disabled, started.Options.Near);
        Assert.Equal(started, workflow.Current.Rules);
        Assert.True(viewModel.IsScanPhase);
    }

    [Fact]
    public async Task ScanButtonReusesOptionsConfirmedEarlierForTheSameFolder()
    {
        var scope = new ExactShellFixture.StaticScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new RecordingScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler);
        var viewModel = new ShellViewModel(scope, workflow);
        await scheduler.Finish(viewModel.ContinueCommand.ExecuteAsync());
        await scheduler.Finish(viewModel.ConfirmExactRulesCommand.ExecuteAsync());
        var confirmed = workflow.Current.Rules;
        Assert.True(viewModel.IsChooseFolderPhase);

        await scheduler.Finish(viewModel.ScanFolderCommand.ExecuteAsync());

        Assert.Equal(confirmed, Assert.Single(scan.Starts));
    }

    [Fact]
    public void ScanButtonIsUnavailableUntilTheFolderIsReady()
    {
        var fixture = new ExactShellFixture();
        var scopeOnly = new ShellViewModel(fixture.Scope);

        Assert.False(scopeOnly.CanScanFolder);
        Assert.False(scopeOnly.CanChangeScanOptions);
    }

    [Fact]
    public void TheRailShowsOneFolderStepForBothFolderPhases()
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            CurrentPhase = WorkflowPhase.ConfirmRules,
            Phases = InitialWorkflow.Create().Select(phase => phase with
            {
                Availability = phase.Phase switch
                {
                    WorkflowPhase.ConfirmRules => PhaseAvailability.Current,
                    WorkflowPhase.ChooseScope => PhaseAvailability.Available,
                    _ => PhaseAvailability.Unavailable,
                },
            }).ToImmutableArray(),
        });

        Assert.Equal(["Choose folder", "Scan", "Review", "Clean up"],
            fixture.ViewModel.RailSteps.Select(phase => phase.DisplayName));
        Assert.Equal([1, 2, 3, 4], fixture.ViewModel.RailSteps.Select(phase => phase.StepNumber));
        Assert.True(fixture.ViewModel.RailSteps[0].IsCurrent);
        Assert.Equal(5, fixture.ViewModel.Phases.Length);
        Assert.True(fixture.ViewModel.Phases[1].IsCurrent);
        Assert.True(fixture.ViewModel.IsChooseFolderPhase);
    }

    private sealed class RecordingScanOwner : IScanCoordinator
    {
        public ScanSnapshot Current { get; } = new(null, ScanStatus.Idle,
            new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0));
        public List<ExactRuleConfirmation> Starts { get; } = [];
        public event EventHandler<ScanSnapshot>? SnapshotChanged { add { } remove { } }

        public Task StartAsync(ShellSnapshot scope, ExactRuleConfirmation rules, CancellationToken token)
        {
            Starts.Add(rules);
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(long scopeRevision, long? ruleSetRevision) => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
