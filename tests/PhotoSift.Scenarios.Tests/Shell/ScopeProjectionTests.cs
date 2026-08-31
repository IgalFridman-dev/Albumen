using System.Collections.Immutable;
using PhotoSift.App.Shell;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class ScopeProjectionTests
{
    [Fact]
    public void ColdSnapshotInvitesOneFolderWithoutClaimingVerification()
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(ColdSnapshot()));

        Assert.Equal("Choose one local folder", viewModel.ScopeStatus);
        Assert.Equal(string.Empty, viewModel.SelectedPath);
        Assert.Equal(string.Empty, viewModel.ResolvedPath);
        Assert.Equal("Unavailable", viewModel.SupportedCount);
        Assert.False(viewModel.IsChecking);
        Assert.False(viewModel.IsReady);
        Assert.False(viewModel.IsBlocked);
        Assert.False(viewModel.IsCanceled);
    }

    [Fact]
    public void CheckingSnapshotShowsTheSelectionAsBusyAndUnverified()
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(CheckingSnapshot()));

        Assert.Equal("Checking location and access", viewModel.ScopeStatus);
        Assert.Equal(@"C:\Photos", viewModel.SelectedPath);
        Assert.Equal(string.Empty, viewModel.ResolvedPath);
        Assert.Equal("Unavailable", viewModel.SupportedCount);
        Assert.True(viewModel.IsChecking);
        Assert.False(viewModel.IsReady);
        Assert.False(viewModel.IsBlocked);
        Assert.False(viewModel.IsCanceled);
    }

    [Fact]
    public void ReadySnapshotShowsTruthWithoutUnlockingLaterPhases()
    {
        var workflow = new RecordingChooseScopeWorkflow(ReadySnapshot());
        var viewModel = new ShellViewModel(workflow);

        Assert.Equal("Ready", viewModel.ScopeStatus);
        Assert.Equal(@"C:\Photos", viewModel.SelectedPath);
        Assert.Equal(@"\\?\C:\Photos", viewModel.ResolvedPath);
        Assert.True(viewModel.IncludeSubfolders);
        Assert.Equal("12", viewModel.SupportedCount);
        Assert.Equal("Not configured", viewModel.RuleSetStatus);
        Assert.True(viewModel.IsReady);
        Assert.False(viewModel.IsChecking);
        Assert.False(viewModel.IsBlocked);
        Assert.False(viewModel.IsCanceled);
        Assert.All(viewModel.Phases.Skip(1), phase => Assert.False(phase.IsAvailable));
    }

    [Fact]
    public void ReadySnapshotLabelsEveryOutcomeAndRestrictionCount()
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(ReadySnapshot()));

        Assert.Equal(
            new[]
            {
                ("Supported images", "12"),
                ("Unsupported extensions", "3"),
                ("Unreadable images", "4"),
                ("Codec unavailable", "5"),
                ("Escaping directory links skipped", "6"),
                ("Duplicate directories skipped", "7"),
            },
            viewModel.ScopeSummary.Select(item => (item.Label, item.Value)));
    }

    [Theory]
    [InlineData(ScopeBlockingReason.UnavailableRoot, "The selected folder is unavailable.")]
    [InlineData(ScopeBlockingReason.NonLocalRoot, "Choose a folder on a local drive.")]
    [InlineData(ScopeBlockingReason.UnsupportedRoot, "The selected drive type is not supported.")]
    [InlineData(ScopeBlockingReason.SynchronizedLocation, "Synchronized folders are not supported.")]
    [InlineData(ScopeBlockingReason.PlaceholderLocation, "Placeholder-backed folders are not supported.")]
    [InlineData(ScopeBlockingReason.RootAccessDenied, "PhotoSift cannot read the selected folder.")]
    [InlineData(ScopeBlockingReason.DescendantAccessDenied, "PhotoSift cannot read every included folder.")]
    [InlineData(ScopeBlockingReason.UnknownSafety, "PhotoSift could not verify this folder safely.")]
    public void BlockedSnapshotShowsAStablePlainLanguageReason(
        ScopeBlockingReason reason,
        string expectedReason)
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(BlockedSnapshot(reason)));

        Assert.Equal("Blocked", viewModel.ScopeStatus);
        Assert.Equal(new[] { expectedReason }, viewModel.BlockingReasons);
        Assert.Equal("Unavailable", viewModel.SupportedCount);
        Assert.True(viewModel.IsBlocked);
        Assert.False(viewModel.IsChecking);
        Assert.False(viewModel.IsReady);
        Assert.False(viewModel.IsCanceled);
    }

    [Fact]
    public void CanceledSnapshotKeepsTheSelectionVisibleButUnverified()
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(CanceledSnapshot()));

        Assert.Equal("Not verified", viewModel.ScopeStatus);
        Assert.Equal(@"C:\Photos", viewModel.SelectedPath);
        Assert.Equal(string.Empty, viewModel.ResolvedPath);
        Assert.Equal("Unavailable", viewModel.SupportedCount);
        Assert.True(viewModel.IsCanceled);
        Assert.False(viewModel.IsChecking);
        Assert.False(viewModel.IsReady);
        Assert.False(viewModel.IsBlocked);
    }

    [Fact]
    public void UnknownScopeStateIsRejectedInsteadOfPresentedAsReady()
    {
        var snapshot = ColdSnapshot() with { ScopeState = (ScopeSessionState)999 };

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ShellViewModel(new RecordingChooseScopeWorkflow(snapshot)));

        Assert.Equal("Unknown Scope state: 999", exception.Message);
    }

    [Fact]
    public async Task ChooseCommandForwardsThroughApplicationAndProjectsItsPublication()
    {
        var workflow = new RecordingChooseScopeWorkflow(ColdSnapshot());
        workflow.OnChoose = () => workflow.Publish(CheckingSnapshot());
        var viewModel = new ShellViewModel(workflow);

        await viewModel.ChooseScopeCommand.ExecuteAsync();

        Assert.Equal(1, workflow.ChooseCallCount);
        Assert.True(viewModel.IsChecking);
        Assert.Equal(@"C:\Photos", viewModel.SelectedPath);
    }

    [Fact]
    public async Task RetryCommandForwardsThroughApplicationAndProjectsItsPublication()
    {
        var workflow = new RecordingChooseScopeWorkflow(CanceledSnapshot());
        workflow.OnRetry = () => workflow.Publish(ReadySnapshot());
        var viewModel = new ShellViewModel(workflow);

        await viewModel.RetryScopeCommand.ExecuteAsync();

        Assert.Equal(1, workflow.RetryCallCount);
        Assert.True(viewModel.IsReady);
        Assert.Equal(@"\\?\C:\Photos", viewModel.ResolvedPath);
    }

    [Fact]
    public async Task RecursionCommandForwardsItsBooleanAndProjectsTheNewSelection()
    {
        var workflow = new RecordingChooseScopeWorkflow(
            CheckingSnapshot(ScopeRecursion.TopLevelOnly));
        workflow.OnChangeRecursion = includeSubfolders => workflow.Publish(
            CheckingSnapshot(
                includeSubfolders
                    ? ScopeRecursion.IncludeSubfolders
                    : ScopeRecursion.TopLevelOnly));
        var viewModel = new ShellViewModel(workflow);

        await viewModel.ChangeRecursionCommand.ExecuteAsync(true);

        Assert.Equal(1, workflow.ChangeRecursionCallCount);
        Assert.True(workflow.ReceivedIncludeSubfolders);
        Assert.True(viewModel.IncludeSubfolders);
        Assert.True(viewModel.IsChecking);
    }

    private static ShellSnapshot ColdSnapshot() =>
        new(
            InitialWorkflow.Create(),
            "Choose one local folder to scan.",
            ScopeSessionState.Cold);

    private static ShellSnapshot CheckingSnapshot(
        ScopeRecursion recursion = ScopeRecursion.IncludeSubfolders) =>
        new(
            InitialWorkflow.Create(),
            "Checking location and access.",
            ScopeSessionState.Checking,
            new ScopeSelection(@"C:\Photos", recursion),
            Revision: 1);

    private static ShellSnapshot ReadySnapshot()
    {
        var selection = new ScopeSelection(
            @"C:\Photos",
            ScopeRecursion.IncludeSubfolders);
        var result = new ScopePreflightResult(
            ScopePreflightDecision.Ready,
            new ResolvedScope(
                selection,
                @"\\?\C:\Photos",
                new DirectoryIdentity(1, 2, 3)),
            new ScopeOutcomeCounts(
                supported: 12,
                unsupportedExtension: 3,
                unreadable: 4,
                codecUnavailable: 5),
            new ScopeRestrictionCounts(
                escapingReparseTargets: 6,
                duplicateDirectoryIdentities: 7),
            ImmutableArray<ScopeBlockingReason>.Empty);
        return new ShellSnapshot(
            InitialWorkflow.Create(),
            "Scope is ready.",
            ScopeSessionState.Ready,
            selection,
            result,
            "Not configured",
            Revision: 1);
    }

    private static ShellSnapshot BlockedSnapshot(ScopeBlockingReason reason)
    {
        var selection = new ScopeSelection(
            @"C:\Photos",
            ScopeRecursion.IncludeSubfolders);
        var result = new ScopePreflightResult(
            ScopePreflightDecision.Blocked,
            new ResolvedScope(
                selection,
                @"\\?\C:\Photos",
                new DirectoryIdentity(1, 2, 3)),
            null,
            new ScopeRestrictionCounts(0, 0),
            ImmutableArray.Create(reason));
        return new ShellSnapshot(
            InitialWorkflow.Create(),
            "Scope is blocked.",
            ScopeSessionState.Blocked,
            selection,
            result,
            "Not configured",
            Revision: 1);
    }

    private static ShellSnapshot CanceledSnapshot() =>
        new(
            InitialWorkflow.Create(),
            "Scope was not verified.",
            ScopeSessionState.Canceled,
            new ScopeSelection(
                @"C:\Photos",
                ScopeRecursion.IncludeSubfolders),
            Revision: 1);

    private sealed class RecordingChooseScopeWorkflow(ShellSnapshot current)
        : IChooseScopeWorkflow
    {
        public event EventHandler<ShellSnapshot>? SnapshotChanged;

        public ShellSnapshot Current { get; private set; } = current;

        public int ChooseCallCount { get; private set; }

        public int RetryCallCount { get; private set; }

        public int ChangeRecursionCallCount { get; private set; }

        public bool? ReceivedIncludeSubfolders { get; private set; }

        public Action? OnChoose { get; set; }

        public Action? OnRetry { get; set; }

        public Action<bool>? OnChangeRecursion { get; set; }

        public Task HandleAsync(
            ChooseScopeCommand command,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            ChooseCallCount++;
            OnChoose?.Invoke();
            return Task.CompletedTask;
        }

        public Task HandleAsync(
            ChangeScopeRecursionCommand command,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            ChangeRecursionCallCount++;
            ReceivedIncludeSubfolders = command.IncludeSubfolders;
            OnChangeRecursion?.Invoke(command.IncludeSubfolders);
            return Task.CompletedTask;
        }

        public Task HandleAsync(
            RetryScopePreflightCommand command,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            RetryCallCount++;
            OnRetry?.Invoke();
            return Task.CompletedTask;
        }

        public void Publish(ShellSnapshot snapshot)
        {
            Current = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }
}
