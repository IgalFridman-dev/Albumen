using PhotoSift.Application.Scan;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Application.Tests.Shell;

public sealed class PhotoSiftWorkflowTests
{
    [Fact]
    public async Task ContinueAndConfirmationDoNotStartWorkAndStartRequiresExplicitConfirmation()
    {
        var scope = new ScopeOwner(Ready(1));
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, new PostedScheduler());
        Assert.Equal(5, workflow.Current.Phases.Length);
        await workflow.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(scan.Starts);
        await workflow.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.ConfirmRules, workflow.Current.CurrentPhase);
        Assert.Single(workflow.Current.Phases, phase => phase.Availability == PhaseAvailability.Current);
        Assert.Equal(PhaseAvailability.Available, workflow.Current.Phases[0].Availability);
        Assert.Null(workflow.Current.Rules);
        await workflow.ConfirmExactRulesAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(workflow.Current.Rules);
        Assert.Empty(scan.Starts);
        await workflow.StartScanAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var started = Assert.Single(scan.Starts);
        Assert.Equal(1, started.ScopeRevision);
        Assert.Equal(workflow.Current.Rules, started);
        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
        await workflow.NavigateAsync(WorkflowPhase.ApproveCleanup).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.Scan, workflow.Current.CurrentPhase);
    }

    [Fact]
    public async Task ColdAndUnconfirmedScopeCannotBypassContinue()
    {
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(new(InitialWorkflow.Create(), "Choose")), new ScanOwner(), new PostedScheduler());
        await workflow.ContinueAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await workflow.ConfirmExactRulesAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await workflow.NavigateAsync(WorkflowPhase.Review).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        Assert.Null(workflow.Current.Rules);
        Assert.All(workflow.Current.Phases.Skip(1), p => Assert.Equal(PhaseAvailability.Unavailable, p.Availability));
    }

    [Fact]
    public async Task AcceptedScopeChangeRevokesRulesAndQueuedOldReviewCannotRestoreThem()
    {
        var scheduler = new ManualScheduler();
        var scope = new ScopeOwner(Ready(1));
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var oldRules = workflow.Current.Rules!;
        var oldScan = EmptyScan(new(Guid.NewGuid(), 1, oldRules.RuleSetRevision, 1), ScanStatus.Complete);
        scan.Publish(oldScan); // This presentation result is queued before acceptance changes.
        scope.Publish(Ready(2) with { ScopeState = ScopeSessionState.Checking });
        scheduler.Drain();
        Assert.Null(workflow.Current.Rules);
        Assert.Null(workflow.Current.Scan.Pin);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        scan.Publish(oldScan);
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.Review));
        Assert.Null(workflow.Current.Scan.Pin);
        scope.Publish(Ready(2));
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        Assert.True(workflow.Current.Rules!.RuleSetRevision > oldRules.RuleSetRevision);
    }

    [Fact]
    public async Task NeutralScopePublicationPreservesReviewButFreshRetryRevisionRevokesIt()
    {
        var scheduler = new ManualScheduler();
        var scope = new ScopeOwner(Ready(1));
        var scan = new ScanOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        var rules = workflow.Current.Rules!;
        scan.Publish(EmptyScan(new(Guid.NewGuid(), 1, rules.RuleSetRevision, 1), ScanStatus.Incomplete));
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.Review));
        scope.Publish(scope.Current);
        scheduler.Drain();
        Assert.Equal(rules, workflow.Current.Rules);
        Assert.Equal(WorkflowPhase.Review, workflow.Current.CurrentPhase);
        scope.Publish(Ready(2) with { ScopeState = ScopeSessionState.Checking });
        scheduler.Drain();
        Assert.Null(workflow.Current.Rules);
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
    }

    [Fact]
    public async Task CancellationDoesNotHoldShellTurn()
    {
        var scheduler = new ManualScheduler();
        var scan = new ScanOwner { Cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), scan, scheduler);
        await scheduler.Finish(workflow.ContinueAsync());
        await scheduler.Finish(workflow.ConfirmExactRulesAsync());
        await scheduler.Finish(workflow.StartScanAsync());
        var cancel = workflow.CancelScanAsync();
        scheduler.Drain();
        await scheduler.Finish(workflow.NavigateAsync(WorkflowPhase.ChooseScope));
        Assert.Equal(WorkflowPhase.ChooseScope, workflow.Current.CurrentPhase);
        scan.Cancellation.SetResult();
        await cancel.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ObserverConfirmationCannotReenterThePublishingTurn()
    {
        var scheduler = new ManualScheduler();
        var workflow = new PhotoSiftWorkflow(new ScopeOwner(Ready(1)), new ScanOwner(), scheduler);
        Task? confirmation = null;
        var publishing = false;
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            Assert.False(publishing);
            publishing = true;
            if (snapshot.CurrentPhase == WorkflowPhase.ConfirmRules && snapshot.Rules is null)
            {
                confirmation = workflow.ConfirmExactRulesAsync();
                scheduler.Drain(); // Model a modal UI pump inside the observer.
                Assert.False(confirmation.IsCompleted);
                Assert.Null(workflow.Current.Rules);
            }
            publishing = false;
        };
        await scheduler.Finish(workflow.ContinueAsync());
        Assert.NotNull(confirmation);
        await confirmation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(workflow.Current.Rules);
    }

    [Fact]
    public async Task RealScopeOwnerKeepsCanceledPickerAndSameRecursionNeutralThenFreshRetryCanScan()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Preflight.Inspect = (selection, _) => Task.FromResult(fixture.Evidence with
        {
            Scope = fixture.Scope with { Selection = selection },
        });
        var scope = new ChooseScopeWorkflow(new CancelAfterSelectionPicker(fixture.Scope.Selection.SelectedPath), fixture.Preflight, fixture.Scheduler);
        var workflow = new PhotoSiftWorkflow(scope, fixture.Owner, fixture.Scheduler);
        await fixture.Pump(scope.HandleAsync(new ChooseScopeCommand(), TestContext.Current.CancellationToken));
        await fixture.Pump(workflow.ContinueAsync());
        await fixture.Pump(workflow.ConfirmExactRulesAsync());
        var rules = workflow.Current.Rules!;
        await fixture.Pump(scope.HandleAsync(new ChooseScopeCommand(), TestContext.Current.CancellationToken));
        await fixture.Pump(scope.HandleAsync(new ChangeScopeRecursionCommand(false), TestContext.Current.CancellationToken));
        Assert.Equal(rules, workflow.Current.Rules);
        await fixture.Pump(workflow.StartScanAsync());
        var first = await fixture.Run();
        await fixture.Pump(workflow.CancelScanAsync());
        await fixture.Pump(scope.HandleAsync(new RetryScopePreflightCommand(), TestContext.Current.CancellationToken));
        Assert.Null(workflow.Current.Rules);
        Assert.Null(workflow.Current.Scan.Pin);
        await fixture.Pump(workflow.ContinueAsync());
        await fixture.Pump(workflow.ConfirmExactRulesAsync());
        fixture.Executor.Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Pump(workflow.StartScanAsync());
        var second = await fixture.Run();
        Assert.Equal(2, second.Context.Pin.ScopeRevision);
        Assert.True(second.Context.Pin.RuleSetRevision > first.Context.Pin.RuleSetRevision);
        Assert.NotEqual(first.Context.Pin.ScanId, second.Context.Pin.ScanId);
    }

    [Fact]
    public void RuleConfirmationBelongsToOneReadyAcceptedScopeRevision()
    {
        var scope = Ready(4);
        Assert.False(ShellPhasePolicy.CanStart(scope, null));
        Assert.False(ShellPhasePolicy.CanStart(scope, new(3, 1)));
        Assert.True(ShellPhasePolicy.CanStart(scope, new(4, 1)));
        Assert.False(ShellPhasePolicy.CanStart(scope with { ScopeState = ScopeSessionState.Checking }, new(4, 1)));
    }

    private static ShellSnapshot Ready(long revision) => new(InitialWorkflow.Create(), "Ready", ScopeSessionState.Ready, Revision: revision);
    private static ScanSnapshot EmptyScan(ScanPin? pin = null, ScanStatus status = ScanStatus.Idle) => new(pin, status,
        new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0));
    private sealed class CancelAfterSelectionPicker(string path) : ILocalFolderPicker
    {
        private bool selected;
        public Task<string?> PickSingleAsync(CancellationToken token)
        {
            var result = selected ? null : path;
            selected = true;
            return Task.FromResult(result);
        }
    }
    private sealed class PostedScheduler : IWorkflowScheduler { public void Post(Action action) => ThreadPool.QueueUserWorkItem(_ => action()); }
    private sealed class ManualScheduler : IWorkflowScheduler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();
        public void Post(Action action) => queue.Enqueue(action);
        public void Drain() { while (queue.TryDequeue(out var action)) action(); }
        public async Task Finish(Task task) { Drain(); await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); Drain(); }
    }
    private sealed class ScopeOwner(ShellSnapshot current) : IChooseScopeWorkflow
    {
        public ShellSnapshot Current { get; private set; } = current;
        public event EventHandler<ShellSnapshot>? SnapshotChanged;
        public void Publish(ShellSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        public Task HandleAsync(ChooseScopeCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(ChangeScopeRecursionCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(RetryScopePreflightCommand command, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ScanOwner : IScanCoordinator
    {
        public ScanSnapshot Current { get; private set; } = EmptyScan();
        public List<ExactRuleConfirmation> Starts { get; } = [];
        public TaskCompletionSource? Cancellation { get; init; }
        public event EventHandler<ScanSnapshot>? SnapshotChanged;
        public void Publish(ScanSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        public Task StartAsync(ShellSnapshot scope, ExactRuleConfirmation rules, CancellationToken token) { Starts.Add(rules); return Task.CompletedTask; }
        public Task InvalidateAsync(long scopeRevision, long? ruleRevision) => Task.CompletedTask;
        public Task CancelAsync() => Cancellation?.Task ?? Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}





