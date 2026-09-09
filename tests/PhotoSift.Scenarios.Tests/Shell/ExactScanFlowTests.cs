using System.Collections.Immutable;
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
    internal sealed class PublishingWorkflow(ShellSnapshot scope) : IPhotoSiftWorkflow
    {
        public PhotoSiftSnapshot Current { get; private set; } = new(scope, null,
            new(null, ScanStatus.Idle, new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0)), WorkflowPhase.ChooseScope, InitialWorkflow.Create());
        public List<string> Actions { get; } = [];
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
        public Task NavigateAsync(WorkflowPhase phase) => Act(phase.ToString());
    }
}





