using System.Collections.Immutable;
using PhotoSift.App.Shell;
using PhotoSift.Application.Scope;
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
        Assert.Equal("Scope blocked", viewModel.BlockingNoticeAutomationName);
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

    [Theory]
    [InlineData(ScopeVolumeKind.Fixed, "Fixed")]
    [InlineData(ScopeVolumeKind.Removable, "Removable")]
    public void ReadySnapshotDistinguishesVerifiedLocalVolume(
        ScopeVolumeKind volume,
        string expectedValue)
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(ReadySnapshot(volume)));

        var volumeSummary = Assert.Single(
            viewModel.ScopeSummary,
            item => item.Label == "Local drive type");
        Assert.Equal(expectedValue, volumeSummary.Value);
        Assert.True(viewModel.IsReady);
    }

    [Fact]
    public void UnknownReadyVolumeIsRejectedInsteadOfPresentedAsReady()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ShellViewModel(
                new RecordingChooseScopeWorkflow(
                    ReadySnapshot((ScopeVolumeKind)999))));
    }

    [Fact]
    public void ReadySnapshotLabelsEveryOutcomeAndRestrictionCount()
    {
        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(ReadySnapshot()));

        Assert.Equal(
            new[]
            {
                ("Local drive type", "Fixed"),
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
        Assert.Equal(expectedReason, viewModel.BlockingNoticeAutomationName);
        Assert.Equal("Unavailable", viewModel.SupportedCount);
        Assert.True(viewModel.IsBlocked);
        Assert.False(viewModel.IsChecking);
        Assert.False(viewModel.IsReady);
        Assert.False(viewModel.IsCanceled);
    }

    [Fact]
    public void BlockedSnapshotWithoutReasonsUsesGenericAutomationFallback()
    {
        var blocked = BlockedSnapshot(ScopeBlockingReason.NonLocalRoot);
        var snapshot = blocked with
        {
            Preflight = blocked.Preflight! with
            {
                BlockingReasons = ImmutableArray<ScopeBlockingReason>.Empty,
            },
        };

        var viewModel = new ShellViewModel(
            new RecordingChooseScopeWorkflow(snapshot));

        Assert.Equal("Scope blocked", viewModel.BlockingNoticeAutomationName);
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

    [Fact]
    public async Task AsyncCommandConstructorsDefaultToRejectingOverlappingInvocation()
    {
        await AssertRejectsOverlappingInvocationByDefault(
            execute => new AsyncCommand(execute));
        await AssertRejectsOverlappingInvocationByDefault(
            execute => new AsyncCommand(_ => execute()));
    }

    [Fact]
    public async Task ChooseDuringCheckingCancelsSupersededOperationAndPublishesOnlyReplacement()
    {
        const string firstPath = @"C:\First";
        const string replacementPath = @"C:\Replacement";
        var picker = new SequenceFolderPicker(firstPath, replacementPath);
        using var preflight = new CancellationAwarePreflight();
        var workflow = new ChooseScopeWorkflow(picker, preflight);
        var terminalSnapshots = new List<ShellSnapshot>();
        var terminalSnapshotsGate = new object();
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.ScopeState is
                ScopeSessionState.Ready or
                ScopeSessionState.Blocked or
                ScopeSessionState.Canceled)
            {
                lock (terminalSnapshotsGate)
                {
                    terminalSnapshots.Add(snapshot);
                }
            }
        };
        var viewModel = new ShellViewModel(workflow);

        var firstOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
        await preflight.FirstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsChecking);
        Assert.Equal(firstPath, viewModel.SelectedPath);
        Assert.True(viewModel.ChooseScopeCommand.CanExecute(parameter: null));

        var replacementOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
        await preflight.ReplacementStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await preflight.FirstCanceled.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        preflight.ReplacementCompletion.SetResult(ReadyEvidence(replacementPath));

        await Task.WhenAll(firstOperation, replacementOperation);

        Assert.True(viewModel.IsReady);
        Assert.Equal(replacementPath, viewModel.SelectedPath);
        Assert.Equal(replacementPath, viewModel.ResolvedPath);
        Assert.All(viewModel.Phases.Skip(1), phase => Assert.False(phase.IsAvailable));
        ShellSnapshot[] observedTerminals;
        lock (terminalSnapshotsGate)
        {
            observedTerminals = [.. terminalSnapshots];
        }

        var replacement = Assert.Single(observedTerminals);
        Assert.Equal(ScopeSessionState.Ready, replacement.ScopeState);
        Assert.Equal(replacementPath, replacement.Selection?.SelectedPath);
        Assert.Equal(2, replacement.Revision);
    }

    [Theory]
    [InlineData(false, ScopeRecursion.TopLevelOnly)]
    [InlineData(true, ScopeRecursion.IncludeSubfolders)]
    public async Task RetryAndRecursionChangeDuringCheckingCancelSupersededOperation(
        bool changeRecursion,
        ScopeRecursion expectedRecursion)
    {
        const string path = @"C:\Photos";
        var picker = new SequenceFolderPicker(path);
        using var preflight = new CancellationAwarePreflight();
        var workflow = new ChooseScopeWorkflow(picker, preflight);
        var viewModel = new ShellViewModel(workflow);

        var firstOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
        await preflight.FirstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var replacementOperation = changeRecursion
            ? viewModel.ChangeRecursionCommand.ExecuteAsync(true)
            : viewModel.RetryScopeCommand.ExecuteAsync();
        await preflight.ReplacementStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await preflight.FirstCanceled.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        preflight.ReplacementCompletion.SetResult(
            ReadyEvidence(path, expectedRecursion));

        await Task.WhenAll(firstOperation, replacementOperation);

        Assert.True(viewModel.IsReady);
        Assert.Equal(expectedRecursion, workflow.Current.Selection?.Recursion);
        Assert.Equal(2, workflow.Current.Revision);
        Assert.All(viewModel.Phases.Skip(1), phase => Assert.False(phase.IsAvailable));
    }

    [Fact]
    public async Task ReentrantCancellationReplacementKeepsTheNewestResultCurrent()
    {
        const string path = @"C:\Photos";
        ShellViewModel? viewModel = null;
        using var preflight = new ReentrantReplacementPreflight(
            () => viewModel!.ChangeRecursionCommand.ExecuteAsync(true));
        var workflow = new ChooseScopeWorkflow(
            new SequenceFolderPicker(path),
            preflight);
        viewModel = new ShellViewModel(workflow);

        var firstOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
        await preflight.FirstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        var supersededOperation = viewModel.RetryScopeCommand.ExecuteAsync();
        var newestOperation = await preflight.ReplacementOperation.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await preflight.ReplacementStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        preflight.ReplacementCompletion.SetResult(
            ReadyEvidence(path, ScopeRecursion.IncludeSubfolders));

        await Task.WhenAll(
            firstOperation,
            supersededOperation,
            newestOperation);

        Assert.True(viewModel.IsReady);
        Assert.Equal(path, viewModel.ResolvedPath);
        Assert.Equal(ScopeSessionState.Ready, workflow.Current.ScopeState);
        Assert.Equal(
            ScopeRecursion.IncludeSubfolders,
            workflow.Current.Selection?.Recursion);
        // Retry is accepted before its asynchronous cancellation callback submits recursion.
        Assert.Equal(3, workflow.Current.Revision);
    }

    [Fact]
    public async Task SameValueRecursionDuringCheckingLeavesActivePreflightRunning()
    {
        const string path = @"C:\Photos";
        var preflight = new CompletablePreflight();
        var workflow = new ChooseScopeWorkflow(
            new SequenceFolderPicker(path),
            preflight);
        var viewModel = new ShellViewModel(workflow);

        var activeOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
        await preflight.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        await viewModel.ChangeRecursionCommand.ExecuteAsync(false);
        preflight.Completion.SetResult(
            ReadyEvidence(path, ScopeRecursion.TopLevelOnly));
        await activeOperation;

        Assert.True(viewModel.IsReady);
        Assert.Equal(path, viewModel.ResolvedPath);
        Assert.Equal(ScopeSessionState.Ready, workflow.Current.ScopeState);
        Assert.Equal(
            ScopeRecursion.TopLevelOnly,
            workflow.Current.Selection?.Recursion);
        Assert.Equal(1, workflow.Current.Revision);
    }

    private static ShellSnapshot ColdSnapshot() =>
        new(
            InitialWorkflow.Create(),
            "Choose one local folder to scan.",
            ScopeSessionState.Cold);

    private static async Task AssertRejectsOverlappingInvocationByDefault(
        Func<Func<Task>, AsyncCommand> commandFactory)
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var command = commandFactory(async () =>
        {
            invocationCount++;
            started.TrySetResult(true);
            await completion.Task;
        });

        var firstInvocation = command.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(command.CanExecute(parameter: null));
        await command.ExecuteAsync();
        Assert.Equal(1, invocationCount);

        completion.SetResult(true);
        await firstInvocation;
        Assert.True(command.CanExecute(parameter: null));
    }

    private static ShellSnapshot CheckingSnapshot(
        ScopeRecursion recursion = ScopeRecursion.IncludeSubfolders) =>
        new(
            InitialWorkflow.Create(),
            "Checking location and access.",
            ScopeSessionState.Checking,
            new ScopeSelection(@"C:\Photos", recursion),
            Revision: 1);

    private static ShellSnapshot ReadySnapshot(
        ScopeVolumeKind volume = ScopeVolumeKind.Fixed)
    {
        var selection = new ScopeSelection(
            @"C:\Photos",
            ScopeRecursion.IncludeSubfolders);
        var result = new ScopePreflightResult(
            Decision: ScopePreflightDecision.Ready,
            Scope: new ResolvedScope(
                selection,
                @"\\?\C:\Photos",
                new DirectoryIdentity(1, 2, 3)),
            Volume: volume,
            CompleteCounts: new ScopeOutcomeCounts(
                supported: 12,
                unsupportedExtension: 3,
                unreadable: 4,
                codecUnavailable: 5),
            Restrictions: new ScopeRestrictionCounts(
                escapingReparseTargets: 6,
                duplicateDirectoryIdentities: 7),
            BlockingReasons: ImmutableArray<ScopeBlockingReason>.Empty);
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
            Decision: ScopePreflightDecision.Blocked,
            Scope: new ResolvedScope(
                selection,
                @"\\?\C:\Photos",
                new DirectoryIdentity(1, 2, 3)),
            Volume: reason is ScopeBlockingReason.NonLocalRoot
                ? ScopeVolumeKind.Remote
                : ScopeVolumeKind.Fixed,
            CompleteCounts: null,
            Restrictions: new ScopeRestrictionCounts(0, 0),
            BlockingReasons: ImmutableArray.Create(reason));
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

    private static ScopePreflightEvidence ReadyEvidence(
        string path,
        ScopeRecursion recursion = ScopeRecursion.TopLevelOnly) =>
        new(
            new ResolvedScope(
                new ScopeSelection(path, recursion),
                path,
                new DirectoryIdentity(1, 0, 1)),
            ScopeVolumeKind.Fixed,
            ScopeCloudState.Local,
            ScopeTraversalState.Complete,
            new ScopeOutcomeCounts(1, 0, 0, 0),
            new ScopeRestrictionCounts(0, 0));

    private sealed class SequenceFolderPicker(params string[] paths)
        : ILocalFolderPicker
    {
        private readonly Queue<string> pending = new(paths);
        private readonly object gate = new();

        public Task<string?> PickSingleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                return Task.FromResult<string?>(pending.Dequeue());
            }
        }
    }

    private sealed class CancellationAwarePreflight : ILocalScopePreflight, IDisposable
    {
        private readonly CancellationTokenSource cleanupCancellation = new();
        private int invocationCount;

        public TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReplacementStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ScopePreflightEvidence> ReplacementCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken)
        {
            var invocation = Interlocked.Increment(ref invocationCount);
            if (invocation == 1)
            {
                FirstStarted.TrySetResult(true);
                using var registration = cancellationToken.Register(
                    () => FirstCanceled.TrySetResult(true));
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    cleanupCancellation.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
                throw new InvalidOperationException(
                    "The canceled first preflight completed unexpectedly.");
            }

            if (invocation == 2)
            {
                ReplacementStarted.TrySetResult(true);
                return await ReplacementCompletion.Task.WaitAsync(cancellationToken);
            }

            throw new InvalidOperationException(
                $"Unexpected preflight invocation: {invocation}.");
        }

        public void Dispose()
        {
            cleanupCancellation.Cancel();
            cleanupCancellation.Dispose();
        }
    }

    private sealed class ReentrantReplacementPreflight(
        Func<Task> launchReplacement)
        : ILocalScopePreflight, IDisposable
    {
        private readonly CancellationTokenSource cleanupCancellation = new();
        private readonly TaskCompletionSource<bool> firstCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int invocationCount;

        public TaskCompletionSource<bool> FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReplacementStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ScopePreflightEvidence> ReplacementCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Task> ReplacementOperation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, cleanupCancellation.Token);
            var invocation = Interlocked.Increment(ref invocationCount);
            if (invocation == 1)
            {
                using var registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        ReplacementOperation.TrySetResult(launchReplacement());
                    }
                    catch (Exception exception)
                    {
                        ReplacementOperation.TrySetException(exception);
                        throw;
                    }
                    finally
                    {
                        firstCancellation.TrySetResult(true);
                    }
                });
                FirstStarted.TrySetResult(true);
                await firstCancellation.Task.WaitAsync(cleanupCancellation.Token);
                throw new OperationCanceledException(cancellationToken);
            }

            if (invocation is 2 or 3 &&
                selection.Recursion is ScopeRecursion.TopLevelOnly)
            {
                // Retry may start before recursion cancels it, or be canceled before entry.
                await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
                throw new InvalidOperationException("The superseded retry completed unexpectedly.");
            }

            if (invocation is 2 or 3 &&
                selection.Recursion is ScopeRecursion.IncludeSubfolders)
            {
                ReplacementStarted.TrySetResult(true);
                return await ReplacementCompletion.Task.WaitAsync(linkedCancellation.Token);
            }

            throw new InvalidOperationException(
                $"Unexpected preflight invocation: {invocation}.");
        }

        public void Dispose()
        {
            cleanupCancellation.Cancel();
            cleanupCancellation.Dispose();
        }
    }

    private sealed class CompletablePreflight : ILocalScopePreflight
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ScopePreflightEvidence> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return await Completion.Task.WaitAsync(cancellationToken);
        }
    }

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
