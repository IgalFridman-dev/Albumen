using System.Collections.Immutable;
using System.Diagnostics;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class ExactScanFlowTests
{
    private static readonly string[] ConfirmationActions = ["continue", "confirm"];
    private static readonly string[] ScanActions = ["continue", "confirm", "start", "cancel", "Review"];

    [Fact]
    public void ReadOnlyReviewDistinguishesPhysicalCopiesAliasesFailuresAndUnknownRecovery()
    {
        var fixture = new ExactShellFixture();
        fixture.PublishReview(ScanStatus.Incomplete);
        var vm = fixture.ViewModel;
        Assert.True(vm.IsReviewPhase);
        Assert.Contains("incomplete", vm.CoverageStatus, StringComparison.OrdinalIgnoreCase);
        var group = Assert.Single(vm.ExactGroups);
        Assert.Equal(2, group.PhysicalFileCount);
        Assert.Equal(1, group.AliasCount);
        Assert.Equal(3, group.Members.Length);
        Assert.Contains("UnknownAllocation", group.PotentialRecovery);
        Assert.Equal(@"C:\Photos\denied.jpg", Assert.Single(vm.CouldNotAnalyze).DiscoveredPath);
        Assert.Contains("AccessDenied", vm.CouldNotAnalyze[0].Outcome);
        Assert.Contains("Analyzed revisions", vm.ReviewDisclaimer);
        Assert.False(vm.Phases.Single(p => p.Phase == WorkflowPhase.ApproveCleanup).IsAvailable);
        fixture.PublishReview(ScanStatus.Complete);
        Assert.Contains("complete", vm.CoverageStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", vm.ScanCountsText);
    }

    [Fact]
    public async Task ViewModelCommandsRouteExplicitActionsAndPreserveScopeOnlyConstruction()
    {
        var fixture = new ExactShellFixture();
        await fixture.ViewModel.ContinueCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.ViewModel.ConfirmExactRulesCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ConfirmationActions, fixture.Workflow.Actions);
        await fixture.ViewModel.StartScanCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.ViewModel.CancelScanCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.ViewModel.OpenReviewCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ScanActions, fixture.Workflow.Actions);
        var scopeOnly = new ShellViewModel(fixture.Scope);
        Assert.True(scopeOnly.IsChooseScopePhase);
        Assert.False(scopeOnly.CanContinue);
        Assert.All(scopeOnly.Phases.Skip(1), p => Assert.False(p.IsAvailable));
    }

    [Fact]
    public async Task OpenReviewCommandAdmitsTheFreshCompletedScanThroughTheRealWorkflow()
    {
        var scope = new ExactShellFixture.StaticScope();
        var scheduler = new ManualWorkflowScheduler();
        var scan = new CompletedScanOwner();
        var review = new OpeningReviewOwner();
        var workflow = new PhotoSiftWorkflow(scope, scan, scheduler, review);
        var viewModel = new ShellViewModel(scope, workflow);

        await scheduler.Finish(viewModel.ContinueCommand.ExecuteAsync());
        await scheduler.Finish(viewModel.ConfirmExactRulesCommand.ExecuteAsync());
        scan.Publish(CompletedScanOwner.CreateCompleted());
        scheduler.Drain();
        await scheduler.Finish(viewModel.OpenReviewCommand.ExecuteAsync());

        Assert.Equal(1, review.OpenCount);
        Assert.NotEqual(Guid.Empty, viewModel.ExactGroups.Single().GroupId);
        Assert.NotNull(workflow.Current.Review.Pin);
        Assert.True(viewModel.IsReviewPhase);
    }

    [Fact]
    public void ClosedReviewProjectionTracksEachNewScanInsteadOfReusingStaleGroups()
    {
        var fixture = new ExactShellFixture();

        fixture.PublishClosedScan(1, @"C:\Photos\first.jpg");
        Assert.Equal(@"C:\Photos\first.jpg", Assert.Single(fixture.ViewModel.ExactGroups).Members[0].DiscoveredPath);

        fixture.PublishClosedScan(2, @"C:\Photos\replacement.jpg");
        Assert.Equal(@"C:\Photos\replacement.jpg", Assert.Single(fixture.ViewModel.ExactGroups).Members[0].DiscoveredPath);
    }

    [Fact]
    public async Task ShellLifetimeDisposesOneInFlightOwnerAndIgnoresLatePublication()
    {
        var fixture = new ExactShellFixture();
        fixture.PublishReview(ScanStatus.Incomplete);
        fixture.Workflow.HoldDisposal = true;
        var before = Assert.Single(fixture.ViewModel.ExactGroups);
        var lifetime = Assert.IsAssignableFrom<IAsyncDisposable>(fixture.ViewModel);

        var first = lifetime.DisposeAsync().AsTask();
        await fixture.Workflow.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var second = lifetime.DisposeAsync().AsTask();
        fixture.PublishClosedScan(7, @"C:\Photos\late.jpg");
        fixture.Workflow.DisposeRelease.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Workflow.DisposeCalls);
        Assert.Same(before, Assert.Single(fixture.ViewModel.ExactGroups));
    }

    [Theory]
    [InlineData(false, 0, "PHOTOSIFT_SMOKE_OK", "")]
    [InlineData(true, 1, "", "PHOTOSIFT_SMOKE_FAIL type=InvalidOperationException")]
    public async Task SmokeSuccessAndFailureDisposeBeforeReturningExitCode(
        bool failProbe, int expectedExitCode, string expectedOutput, string expectedError)
    {
        var fixture = new ExactShellFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await PhotoSift.App.App.RunSmokeAsync(fixture.ViewModel, _ => failProbe
            ? throw new InvalidOperationException("probe failure")
            : "PHOTOSIFT_SMOKE_OK", output, error);

        Assert.Equal(expectedExitCode, exitCode);
        if (expectedOutput.Length == 0) Assert.Empty(output.ToString());
        else Assert.Contains(expectedOutput, output.ToString(), StringComparison.Ordinal);
        if (expectedError.Length == 0) Assert.Empty(error.ToString());
        else Assert.Contains(expectedError, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Workflow.DisposeCalls);
    }
}

internal sealed class ExactShellFixture
{
    public StaticScope Scope { get; } = new();
    public PublishingWorkflow Workflow { get; }
    public ShellViewModel ViewModel { get; }
    public ExactShellFixture()
    {
        Workflow = new(Scope.Current);
        ViewModel = new(Scope, Workflow);
    }
    public void PublishReview(ScanStatus status, int groupCount = 1, int memberCount = 3)
    {
        var pin = new ScanPin(Guid.NewGuid(), 1, 1, 1);
        var a = Row(pin, 1, 1, @"C:\Photos\one.jpg");
        var alias = Row(pin, 2, 1, @"C:\Photos\alias.jpg");
        var b = Row(pin, 3, 2, @"C:\Photos\two.jpg");
        var failed = new ScanFileOccurrence(new(pin, 4, 1, 1), @"C:\Photos\denied.jpg", @"C:\Photos\denied.jpg", new PhysicalFileKey(1, new(0, 3)), new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.FailedRetryable, ScanReason.AccessDenied), null, new(null, null, false));
        var members = memberCount == 3 ? ImmutableArray.Create(a, alias, b)
            : Enumerable.Range(1, memberCount).Select(i => Row(pin, i, (ulong)i, $@"C:\Photos\copy-{i}.jpg")).ToImmutableArray();
        var groups = Enumerable.Range(0, groupCount).Select(_ => new CandidateGroup(Guid.NewGuid(), 1, pin, CandidateCategory.Exact,
            members, [], false, null, new(null, RecoveryUnavailableReason.UnknownAllocation))).ToImmutableArray();
        var scan = new ScanSnapshot(pin, status, new(ScanStageState.Succeeded, ScanReason.None), [a, alias, b, failed], groups, new(4, 3, 0, 1, 0, 0));
        var reviewGroups = groups.Select(group => new ReviewGroupSnapshot(group,
            group.Members.Select(member => new ReviewMemberSnapshot(member, ReviewEligibilityState.Eligible,
                ReviewUnavailableReason.None, false)).ToImmutableArray(), [],
            new(null, SelectionRecoveryUnavailableReason.UnknownAllocation), false, null)).ToImmutableArray();
        var review = new ReviewSnapshot(new(Guid.NewGuid(), pin), ReviewStatus.Ready, status, 1, reviewGroups,
            new(0, 1, 0, 0, 0, 0, groupCount), false, null, ReviewUnavailableReason.None);
        Workflow.Publish(new PhotoSiftSnapshot(Scope.Current, new(1, 1), scan, WorkflowPhase.Review,
            InitialWorkflow.Create()) { Review = review });
    }
    public void PublishPhase(WorkflowPhase phase, ScanStatus status = ScanStatus.Idle)
    {
        Workflow.Publish(Workflow.Current with { CurrentPhase = phase, Rules = new(1, 1), Scan = Workflow.Current.Scan with { Status = status } });
    }
    public void PublishClosedScan(long occurrenceId, string path)
    {
        var pin = new ScanPin(Guid.NewGuid(), 1, 1, occurrenceId);
        var row = Row(pin, occurrenceId, (ulong)occurrenceId, path);
        var group = new CandidateGroup(Guid.NewGuid(), occurrenceId, pin, CandidateCategory.Exact,
            [row], [], false, null, new(17, RecoveryUnavailableReason.None));
        var scan = new ScanSnapshot(pin, ScanStatus.Complete, new(ScanStageState.Succeeded, ScanReason.None),
            [row], [group], new(1, 1, 0, 0, 0, 0));
        Workflow.Publish(new(Scope.Current, new(1, 1), scan, WorkflowPhase.Review, InitialWorkflow.Create()));
    }
    private static ScanFileOccurrence Row(ScanPin pin, long id, ulong physical, string path)
    {
        var key = new PhysicalFileKey(1, new(0, physical));
        return new(new(pin, id, 1, 1), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 17, new string('A', 64)), new(null, 1, true));
    }
    internal sealed class StaticScope : IChooseScopeWorkflow
    {
        public ShellSnapshot Current { get; } = new(InitialWorkflow.Create(), "Ready", ScopeSessionState.Ready,
            new(@"C:\Photos", ScopeRecursion.TopLevelOnly), Revision: 1);
        public event EventHandler<ShellSnapshot>? SnapshotChanged { add { } remove { } }
        public Task HandleAsync(ChooseScopeCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(ChangeScopeRecursionCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HandleAsync(RetryScopePreflightCommand command, CancellationToken token) => Task.CompletedTask;
    }
    internal sealed class PublishingWorkflow(ShellSnapshot scope) : IPhotoSiftWorkflow, IAsyncDisposable
    {
        public PhotoSiftSnapshot Current { get; private set; } = new(scope, null,
            new(null, ScanStatus.Idle, new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0)), WorkflowPhase.ChooseScope, InitialWorkflow.Create());
        public List<string> Actions { get; } = [];
        public bool HoldDisposal { get; set; }
        public Exception? DisposalFailure { get; set; }
        public int DisposeCalls { get; private set; }
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<PhotoSiftSnapshot>? SnapshotChanged;
        public void Publish(PhotoSiftSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        private Task Act(string action) { Actions.Add(action); return Task.CompletedTask; }
        public Task ContinueAsync() => Act("continue");
        public Task ConfirmExactRulesAsync() => Act("confirm");
        public Task StartScanAsync() => Act("start");
        public Task CancelScanAsync() => Act("cancel");
        public Task OpenReviewAsync() => Act("Review");
        public Task<ReviewCommandResult> ChangeReviewSelectionAsync(SelectionCommand command, CancellationToken token) =>
            Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
        public Task<ReviewCommandResult> AffirmReviewOverrideAsync(OverrideAffirmationCommand command, CancellationToken token) =>
            Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
        public Task<ReviewCommandResult> ApproveReviewAsync(ApproveSelectionCommand command, CancellationToken token) =>
            Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
        public Task RevalidateUnknownAsync(Guid requestId, CancellationToken token) => Task.CompletedTask;
        public Task NavigateAsync(WorkflowPhase phase) => Act(phase.ToString());
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            if (HoldDisposal) await DisposeRelease.Task;
            if (DisposalFailure is not null) throw DisposalFailure;
        }
    }
}

internal sealed class ManualWorkflowScheduler : IWorkflowScheduler
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pending = new();
    public void Post(Action callback) => pending.Enqueue(callback);
    public void Drain()
    {
        for (var turn = 0; turn < 100; turn++)
        {
            if (!pending.TryDequeue(out var callback)) return;
            callback();
        }
        Assert.True(pending.IsEmpty, "The manual workflow queue did not drain after one hundred bounded turns.");
    }
    public async Task Finish(Task task)
    {
        var started = Stopwatch.GetTimestamp();
        while (!task.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
        {
            Drain();
            await Task.Yield();
        }
        Assert.True(task.IsCompleted, "The manual workflow did not settle before the two-second deadline.");
        await task;
        Drain();
    }
}

internal sealed class CompletedScanOwner : IScanCoordinator
{
    public ScanSnapshot Current { get; private set; } = new(null, ScanStatus.Idle,
        new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0));
    public event EventHandler<ScanSnapshot>? SnapshotChanged;
    public void Publish(ScanSnapshot snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
    public Task StartAsync(ShellSnapshot scope, ExactRuleConfirmation rules, CancellationToken token) => Task.CompletedTask;
    public Task InvalidateAsync(long scopeRevision, long? ruleSetRevision) => Task.CompletedTask;
    public Task CancelAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ScanSnapshot CreateCompleted()
    {
        var pin = new ScanPin(Guid.Parse("71000000-0000-0000-0000-000000000001"), 1, 1, 1);
        var key = new PhysicalFileKey(7, new(0, 71));
        var row = new ScanFileOccurrence(new(pin, 71, 1, 1), @"C:\Photos\fresh.jpg", @"C:\Photos\fresh.jpg", key,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 17, new string('A', 64)), new(17, 1, true));
        var group = new CandidateGroup(Guid.Parse("72000000-0000-0000-0000-000000000002"), 1, pin,
            CandidateCategory.Exact, [row], [], false, null, new(17, RecoveryUnavailableReason.None));
        return new(pin, ScanStatus.Complete, new(ScanStageState.Succeeded, ScanReason.None), [row], [group],
            new(1, 1, 0, 0, 0, 0));
    }
}

internal sealed class OpeningReviewOwner : IReviewCoordinator
{
    public ReviewSnapshot Current { get; private set; } = ReviewSnapshot.Closed;
    public int OpenCount { get; private set; }
    public event EventHandler<ReviewSnapshot>? SnapshotChanged;
    public void Publish(ReviewSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }
    public Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken)
    {
        OpenCount++;
        var groups = request.Scan.Groups.Select(group => new ReviewGroupSnapshot(group,
            group.Members.Select(member => new ReviewMemberSnapshot(member, ReviewEligibilityState.Eligible,
                ReviewUnavailableReason.None, false)).ToImmutableArray(), [], new(0, SelectionRecoveryUnavailableReason.None),
            false, null)).ToImmutableArray();
        Current = new(new(Guid.Parse("73000000-0000-0000-0000-000000000003"), request.Scan.Pin!),
            ReviewStatus.Ready, request.Scan.Status, 1, groups, new(0, 0, 0, 0, 0, 0, 1), false, null,
            ReviewUnavailableReason.None);
        SnapshotChanged?.Invoke(this, Current);
        return Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Committed, Current));
    }
    public Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current));
    public Task<ReviewCommandResult> AffirmOverrideAsync(OverrideAffirmationCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current));
    public Task<ReviewCommandResult> ApproveAsync(ApproveSelectionCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current));
    public Task ReportObserverFaultAsync(ReviewSessionPin session) => Task.CompletedTask;
    public Task InvalidateAsync(ScanPin? currentScan) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}




