using System.Collections.Immutable;
using Albumen.App.Shell;
using Albumen.Application.Scan;
using Albumen.Application.Shell;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// PM-009 PR 3c: the folder and the scan options share one screen with one Scan button.
public sealed class ScanFolderTests
{
    [Fact]
    public async Task OneScanButtonConfirmsTheShownOptionsAndStartsTheScan()
    {
        var scope = new ExactShellFixture.StaticScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new RecordingScanOwner();
        var workflow = new AlbumenWorkflow(scope, scan, scheduler);
        var viewModel = new ShellViewModel(scope, workflow);

        Assert.True(viewModel.IsChooseFolderPhase);
        Assert.True(viewModel.CanChangeScanOptions);
        Assert.Equal("Scan 2 photos", viewModel.ScanFolderText);
        Assert.Empty(scan.Starts);

        Assert.True(viewModel.CanScanFolder);
        await scheduler.Finish(viewModel.ScanFolderCommand.ExecuteAsync());

        // Look-alikes are always found: every scan uses the default options.
        var started = Assert.Single(scan.Starts);
        Assert.Equal(ScanOptions.Default, started.Options);
        Assert.Equal(NearDuplicateMode.Conservative, started.Options.Near);
        Assert.Equal(started, workflow.Current.Rules);
        Assert.True(viewModel.IsScanPhase);
    }

    [Fact]
    public async Task ScanButtonReusesOptionsConfirmedEarlierForTheSameFolder()
    {
        var scope = new ExactShellFixture.StaticScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new RecordingScanOwner();
        var workflow = new AlbumenWorkflow(scope, scan, scheduler);
        var viewModel = new ShellViewModel(scope, workflow);
        await scheduler.Finish(viewModel.ContinueCommand.ExecuteAsync());
        await scheduler.Finish(viewModel.ConfirmExactRulesCommand.ExecuteAsync());
        var confirmed = workflow.Current.Rules;
        Assert.True(viewModel.IsChooseFolderPhase);

        await scheduler.Finish(viewModel.ScanFolderCommand.ExecuteAsync());

        Assert.Equal(confirmed, Assert.Single(scan.Starts));
    }

    [Fact]
    public async Task ScanButtonReusesConfirmedOptionsAfterGoingBackToTheFolderStep()
    {
        var scope = new ExactShellFixture.StaticScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new RecordingScanOwner();
        var workflow = new AlbumenWorkflow(scope, scan, scheduler);
        var viewModel = new ShellViewModel(scope, workflow);
        await scheduler.Finish(viewModel.ContinueCommand.ExecuteAsync());
        await scheduler.Finish(viewModel.ConfirmExactRulesCommand.ExecuteAsync());
        var confirmed = workflow.Current.Rules;
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
        Assert.True(viewModel.IsChooseScopePhase);

        await scheduler.Finish(viewModel.ScanFolderCommand.ExecuteAsync());

        Assert.Equal(confirmed, Assert.Single(scan.Starts));
        Assert.True(viewModel.IsScanPhase);
    }

    [Fact]
    public async Task ABlockedFolderCannotBeScanned()
    {
        var scope = new BlockedScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new RecordingScanOwner();
        var workflow = new AlbumenWorkflow(scope, scan, scheduler);
        var viewModel = new ShellViewModel(scope, workflow);

        Assert.True(viewModel.IsChooseFolderPhase);
        Assert.False(viewModel.CanScanFolder);
        await scheduler.Finish(viewModel.ScanFolderCommand.ExecuteAsync());

        Assert.Empty(scan.Starts);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Null(workflow.Current.Rules);
    }

    [Fact]
    public async Task NothingStartsWhileAScanIsStillRunning()
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            CurrentPhase = WorkflowPhase.ChooseScope,
            Scan = fixture.Workflow.Current.Scan with { Status = ScanStatus.Running },
        });

        Assert.True(fixture.ViewModel.IsChooseFolderPhase);
        Assert.False(fixture.ViewModel.CanScanFolder);
        await fixture.ViewModel.ScanFolderCommand.ExecuteAsync();

        Assert.Empty(fixture.Workflow.Actions);
    }

    [Fact]
    public void ASubfoldersChangeAfterAScanWarnsThatResultsAndMarksAreCleared()
    {
        var fixture = new ExactShellFixture();
        Assert.DoesNotContain("clears the current results", fixture.ViewModel.ScanOptionsGuidance, StringComparison.Ordinal);

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Pin = new(Guid.Parse("86000000-0000-0000-0000-000000000002"), 1, 1, 1),
                Status = ScanStatus.Complete,
            },
        });

        Assert.Equal("Identical copies and look-alikes are always found. Nothing is marked for you."
            + " Changing Include subfolders clears the current results and your marks, and you scan again.",
            fixture.ViewModel.ScanOptionsGuidance);
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

    private sealed class BlockedScope : IChooseScopeWorkflow
    {
        public ShellSnapshot Current { get; } = CreateBlockedScope();
        public event EventHandler<ShellSnapshot>? SnapshotChanged { add { } remove { } }
        public Task HandleAsync(ChooseScopeCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(ChangeScopeRecursionCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(RetryScopePreflightCommand command, CancellationToken token) => Task.CompletedTask;

        private static ShellSnapshot CreateBlockedScope()
        {
            var selection = new ScopeSelection(@"\\server\Photos", ScopeRecursion.TopLevelOnly);
            var result = new ScopePreflightResult(
                Decision: ScopePreflightDecision.Blocked,
                Scope: new ResolvedScope(selection, @"\\server\Photos", new DirectoryIdentity(1, 2, 3)),
                Volume: ScopeVolumeKind.Remote,
                CompleteCounts: null,
                Restrictions: new ScopeRestrictionCounts(0, 0),
                BlockingReasons: ImmutableArray.Create(ScopeBlockingReason.NonLocalRoot));
            return new(InitialWorkflow.Create(), "This folder can't be scanned yet.",
                ScopeSessionState.Blocked, selection, result, Revision: 1);
        }
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
