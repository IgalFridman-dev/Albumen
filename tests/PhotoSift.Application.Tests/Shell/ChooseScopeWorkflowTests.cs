using PhotoSift.Application.Scope;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Application.Tests.Shell;

public sealed class ChooseScopeWorkflowTests
{
    [Fact]
    public void NewWorkflowStartsColdWithoutRetainedScope()
    {
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(null),
            new StubPreflight(ReadyEvidence(@"C:\Photos")));

        Assert.Equal(ScopeSessionState.Cold, workflow.Current.ScopeState);
        Assert.Null(workflow.Current.Selection);
        Assert.Null(workflow.Current.Preflight);
        Assert.Equal("Not configured", workflow.Current.RuleSetStatus);
        Assert.Equal(0, workflow.Current.Revision);
        Assert.Equal("Choose one local folder to scan.", workflow.Current.Instruction);
        Assert.Equal(PhaseAvailability.Current, workflow.Current.Phases[0].Availability);
        Assert.All(
            workflow.Current.Phases.Skip(1),
            phase => Assert.Equal(PhaseAvailability.Unavailable, phase.Availability));
    }

    [Fact]
    public async Task CancelingFolderPickerLeavesCurrentSnapshotUnchanged()
    {
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(null),
            new StubPreflight(ReadyEvidence(@"C:\Photos")));
        var original = workflow.Current;
        var eventCount = 0;
        workflow.SnapshotChanged += (_, _) => eventCount++;

        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);

        Assert.Same(original, workflow.Current);
        Assert.Equal(0, workflow.Current.Revision);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task ChoosingFolderPublishesCheckingThenReady()
    {
        var picker = new StubFolderPicker(@"C:\Photos");
        var preflight = new StubPreflight(ReadyEvidence(@"C:\Photos"));
        var workflow = new ChooseScopeWorkflow(picker, preflight);
        var states = new List<ScopeSessionState>();
        workflow.SnapshotChanged += (_, snapshot) => states.Add(snapshot.ScopeState);

        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);

        Assert.Equal([ScopeSessionState.Checking, ScopeSessionState.Ready], states);
        Assert.Equal("Not configured", workflow.Current.RuleSetStatus);
        Assert.Equal(1, workflow.Current.Revision);
        Assert.Equal(@"C:\Photos", workflow.Current.Selection?.SelectedPath);
        Assert.Equal(ScopeRecursion.TopLevelOnly, workflow.Current.Selection?.Recursion);
        Assert.Equal(ScopePreflightDecision.Ready, workflow.Current.Preflight?.Decision);
        Assert.All(
            workflow.Current.Phases.Skip(1),
            phase => Assert.Equal(PhaseAvailability.Unavailable, phase.Availability));
    }

    [Fact]
    public async Task LateCompletionCannotReplaceTheNewerRevision()
    {
        var firstCompletion = NewCompletion();
        var secondCompletion = NewCompletion();
        using var preflight = new DelayedPreflight(firstCompletion, secondCompletion);
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            preflight);
        var states = new List<ScopeSessionState>();
        workflow.SnapshotChanged += (_, snapshot) => states.Add(snapshot.ScopeState);

        var firstOperation = workflow.HandleAsync(
            new ChooseScopeCommand(),
            CancellationToken.None);
        await preflight.WaitForStartAsync(1);
        var secondOperation = workflow.HandleAsync(
            new ChooseScopeCommand(),
            CancellationToken.None);
        await preflight.WaitForStartAsync(2);

        secondCompletion.SetResult(ReadyEvidence(@"C:\Photos"));
        await secondOperation;
        var newerSnapshot = workflow.Current;

        firstCompletion.SetResult(BlockedEvidence(@"C:\Photos"));
        await firstOperation;

        Assert.Same(newerSnapshot, workflow.Current);
        Assert.Equal(2, workflow.Current.Revision);
        Assert.Equal(ScopeSessionState.Ready, workflow.Current.ScopeState);
        Assert.Equal(
            [ScopeSessionState.Checking, ScopeSessionState.Checking, ScopeSessionState.Ready],
            states);
    }

    [Fact]
    public async Task ReentrantSelectionCannotDeliverAnOlderSnapshotAfterANewerRevision()
    {
        var nextFolder = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new SequenceFolderPicker(
            Task.FromResult<string?>(@"C:\First"), nextFolder.Task);
        var firstCompletion = NewCompletion();
        var secondCompletion = NewCompletion();
        using var preflight = new DelayedPreflight(firstCompletion, secondCompletion);
        var workflow = new ChooseScopeWorkflow(picker, preflight);
        var observedRevisions = new List<long>();
        Task? reentrantSelection = null;

        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Revision == 1 &&
                snapshot.ScopeState is ScopeSessionState.Checking)
            {
                reentrantSelection = workflow.HandleAsync(
                    new ChooseScopeCommand(),
                    CancellationToken.None);
            }
        };
        workflow.SnapshotChanged += (_, snapshot) =>
            observedRevisions.Add(snapshot.Revision);

        var firstOperation = workflow.HandleAsync(
            new ChooseScopeCommand(), CancellationToken.None);
        try
        {
            await preflight.WaitForStartAsync(1);
            Assert.NotNull(reentrantSelection);
            nextFolder.SetResult(@"C:\Second");
            await preflight.WaitForStartAsync(2);
            secondCompletion.SetResult(ReadyEvidence(@"C:\Second"));
            await reentrantSelection;
            firstCompletion.SetResult(ReadyEvidence(@"C:\First"));
            await firstOperation;
        }
        finally
        {
            nextFolder.TrySetResult(null);
        }

        Assert.Equal([1, 2, 2], observedRevisions);
        Assert.Equal(@"C:\Second", workflow.Current.Selection?.SelectedPath);
        Assert.Equal(2, workflow.Current.Revision);
    }

    [Fact]
    public async Task FolderSelectionStartedFromRetryPublicationSupersedesTheRetry()
    {
        var nextFolder = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new SequenceFolderPicker(
            Task.FromResult<string?>(@"C:\First"), nextFolder.Task);
        var initialCompletion = NewCompletion();
        initialCompletion.SetResult(ReadyEvidence(@"C:\First"));
        var retryCompletion = NewCompletion();
        var replacementCompletion = NewCompletion();
        using var preflight = new DelayedPreflight(
            initialCompletion, retryCompletion, replacementCompletion);
        var workflow = new ChooseScopeWorkflow(picker, preflight);
        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);
        var observedRevisions = new List<long>();
        Task? newerSelection = null;

        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Revision == 2 &&
                snapshot.ScopeState is ScopeSessionState.Checking)
            {
                newerSelection = workflow.HandleAsync(
                    new ChooseScopeCommand(),
                    CancellationToken.None);
            }
        };
        workflow.SnapshotChanged += (_, snapshot) =>
            observedRevisions.Add(snapshot.Revision);

        var retryOperation = workflow.HandleAsync(
            new RetryScopePreflightCommand(),
            CancellationToken.None);
        try
        {
            await preflight.WaitForStartAsync(2);
            Assert.NotNull(newerSelection);
            nextFolder.SetResult(@"C:\Second");
            await preflight.WaitForStartAsync(3);
            replacementCompletion.SetResult(ReadyEvidence(@"C:\Second"));
            await newerSelection;
            retryCompletion.SetResult(ReadyEvidence(@"C:\First"));
            await retryOperation;
        }
        finally
        {
            nextFolder.TrySetResult(null);
        }

        Assert.Equal([2, 3, 3], observedRevisions);
        Assert.Equal(@"C:\Second", workflow.Current.Selection?.SelectedPath);
        Assert.Equal(3, workflow.Current.Revision);
        Assert.Equal(ScopeSessionState.Ready, workflow.Current.ScopeState);
    }

    [Fact]
    public async Task ThrowingSubscriberCannotStrandReentrantSnapshotChanges()
    {
        var picker = new SequenceFolderPicker(@"C:\First", @"C:\Second");
        var workflow = new ChooseScopeWorkflow(picker, new SelectionPreflight());
        var callbackFailure = new InvalidOperationException("subscriber failed");
        var observed = new List<(long Revision, ScopeSessionState State)>();
        Task? reentrantSelection = null;

        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Revision == 1 &&
                snapshot.ScopeState is ScopeSessionState.Checking)
            {
                reentrantSelection = workflow.HandleAsync(
                    new ChooseScopeCommand(),
                    CancellationToken.None);
                throw callbackFailure;
            }
        };
        workflow.SnapshotChanged += (_, snapshot) =>
            observed.Add((snapshot.Revision, snapshot.ScopeState));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.HandleAsync(
                new ChooseScopeCommand(),
                CancellationToken.None));
        Assert.NotNull(reentrantSelection);
        await reentrantSelection;

        Assert.Same(callbackFailure, exception);
        Assert.Equal(
            [
                (2, ScopeSessionState.Checking),
                (2, ScopeSessionState.Ready),
            ],
            observed);
        Assert.Equal(2, workflow.Current.Revision);
        Assert.Equal(ScopeSessionState.Ready, workflow.Current.ScopeState);
    }

    [Theory]
    [InlineData(
        ScopeVolumeKind.Fixed,
        ScopeSessionState.Ready,
        ScopePreflightDecision.Ready)]
    [InlineData(
        ScopeVolumeKind.Remote,
        ScopeSessionState.Blocked,
        ScopePreflightDecision.Blocked)]
    public async Task CompletionSubscriberCancellationCannotReplaceTerminalSnapshot(
        ScopeVolumeKind volume,
        ScopeSessionState expectedState,
        ScopePreflightDecision expectedDecision)
    {
        var evidence = ReadyEvidence(@"C:\Photos") with { Volume = volume };
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            new StubPreflight(evidence));
        var subscriberFailure = new OperationCanceledException(
            "completion subscriber canceled");
        workflow.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.ScopeState == expectedState)
            {
                throw subscriberFailure;
            }
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => workflow.HandleAsync(
                new ChooseScopeCommand(),
                CancellationToken.None));

        Assert.Same(subscriberFailure, exception);
        Assert.Equal(expectedState, workflow.Current.ScopeState);
        Assert.Equal(expectedDecision, workflow.Current.Preflight?.Decision);
        Assert.All(
            workflow.Current.Phases.Skip(1),
            phase => Assert.Equal(PhaseAvailability.Unavailable, phase.Availability));
    }

    [Fact]
    public async Task ChangingRecursionInvalidatesReadyBeforePreflightCompletes()
    {
        var initialCompletion = NewCompletion();
        initialCompletion.SetResult(ReadyEvidence(@"C:\Photos"));
        var changedCompletion = NewCompletion();
        using var preflight = new DelayedPreflight(initialCompletion, changedCompletion);
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            preflight);
        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);

        var changeOperation = workflow.HandleAsync(
            new ChangeScopeRecursionCommand(IncludeSubfolders: true),
            CancellationToken.None);
        await preflight.WaitForStartAsync(2);

        Assert.Equal(ScopeSessionState.Checking, workflow.Current.ScopeState);
        Assert.Equal(ScopeRecursion.IncludeSubfolders, workflow.Current.Selection?.Recursion);
        Assert.Null(workflow.Current.Preflight);
        Assert.Equal(2, workflow.Current.Revision);

        changedCompletion.SetResult(ReadyEvidence(
            @"C:\Photos",
            ScopeRecursion.IncludeSubfolders));
        await changeOperation;
    }

    [Fact]
    public async Task SameRecursionValueLeavesReadySnapshotUnchanged()
    {
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            new StubPreflight(ReadyEvidence(@"C:\Photos")));
        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);
        var ready = workflow.Current;
        var eventCount = 0;
        workflow.SnapshotChanged += (_, _) => eventCount++;

        await workflow.HandleAsync(
            new ChangeScopeRecursionCommand(IncludeSubfolders: false),
            CancellationToken.None);

        Assert.Same(ready, workflow.Current);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public async Task RetryWhileTheFolderPickerIsOpenDoesNothingAndKeepsThePick()
    {
        var nextFolder = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new SequenceFolderPicker(
            Task.FromResult<string?>(@"C:\First"), nextFolder.Task);
        var workflow = new ChooseScopeWorkflow(picker, new SelectionPreflight());
        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);
        Assert.Equal(1, workflow.Current.Revision);
        var observed = new List<(long Revision, ScopeSessionState State)>();
        workflow.SnapshotChanged += (_, snapshot) => observed.Add((snapshot.Revision, snapshot.ScopeState));
        var picking = workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);
        try
        {
            await workflow.HandleAsync(new RetryScopePreflightCommand(), CancellationToken.None);

            Assert.Empty(observed);
            Assert.False(picking.IsCompleted);
            Assert.Equal(ScopeSessionState.Ready, workflow.Current.ScopeState);
            Assert.Equal(1, workflow.Current.Revision);

            nextFolder.SetResult(@"C:\Second");
            await picking;
        }
        finally
        {
            nextFolder.TrySetResult(null);
        }

        Assert.Equal(@"C:\Second", workflow.Current.Selection?.SelectedPath);
        Assert.Equal(2, workflow.Current.Revision);
        Assert.Equal(
            new[] { (2L, ScopeSessionState.Checking), (2L, ScopeSessionState.Ready) },
            observed);
    }

    [Fact]
    public async Task RetryWithoutSelectionIsRejected()
    {
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(null),
            new StubPreflight(ReadyEvidence(@"C:\Photos")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.HandleAsync(
                new RetryScopePreflightCommand(),
                CancellationToken.None));

        Assert.Contains("Scope", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ScopeSessionState.Cold, workflow.Current.ScopeState);
        Assert.Equal(0, workflow.Current.Revision);
    }

    [Fact]
    public async Task RetryInvalidatesCompletedPreflightBeforeReplacementCompletes()
    {
        var initialCompletion = NewCompletion();
        initialCompletion.SetResult(ReadyEvidence(@"C:\Photos"));
        var retryCompletion = NewCompletion();
        using var preflight = new DelayedPreflight(initialCompletion, retryCompletion);
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            preflight);
        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);

        var retryOperation = workflow.HandleAsync(
            new RetryScopePreflightCommand(),
            CancellationToken.None);
        await preflight.WaitForStartAsync(2);

        Assert.Equal(ScopeSessionState.Checking, workflow.Current.ScopeState);
        Assert.Equal(@"C:\Photos", workflow.Current.Selection?.SelectedPath);
        Assert.Null(workflow.Current.Preflight);
        Assert.Equal(2, workflow.Current.Revision);

        retryCompletion.SetResult(ReadyEvidence(@"C:\Photos"));
        await retryOperation;
    }

    [Fact]
    public async Task CanceledPreflightRetainsSelectionWithoutEvidence()
    {
        var completion = NewCompletion();
        using var preflight = new DelayedPreflight(completion);
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            preflight);
        var states = new List<ScopeSessionState>();
        workflow.SnapshotChanged += (_, snapshot) => states.Add(snapshot.ScopeState);

        var operation = workflow.HandleAsync(
            new ChooseScopeCommand(),
            CancellationToken.None);
        completion.SetCanceled(TestContext.Current.CancellationToken);

        await operation;

        Assert.Equal([ScopeSessionState.Checking, ScopeSessionState.Canceled], states);
        Assert.Equal(ScopeSessionState.Canceled, workflow.Current.ScopeState);
        Assert.Equal(@"C:\Photos", workflow.Current.Selection?.SelectedPath);
        Assert.Null(workflow.Current.Preflight);
        Assert.Equal(1, workflow.Current.Revision);
    }

    [Fact]
    public async Task NonCancellationFailureRemainsVisibleToTheCaller()
    {
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            new ThrowingPreflight(new InvalidOperationException("adapter failed")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => workflow.HandleAsync(
                new ChooseScopeCommand(),
                CancellationToken.None));

        Assert.Equal("adapter failed", exception.Message);
        Assert.Equal(ScopeSessionState.Checking, workflow.Current.ScopeState);
        Assert.Null(workflow.Current.Preflight);
    }

    [Fact]
    public async Task BlockingEvidencePublishesBlockedWithoutUnlockingLaterPhases()
    {
        var workflow = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            new StubPreflight(BlockedEvidence(@"C:\Photos")));

        await workflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);

        Assert.Equal(ScopeSessionState.Blocked, workflow.Current.ScopeState);
        Assert.Equal(ScopePreflightDecision.Blocked, workflow.Current.Preflight?.Decision);
        Assert.Equal(
            [ScopeBlockingReason.NonLocalRoot],
            workflow.Current.Preflight?.BlockingReasons);
        Assert.All(
            workflow.Current.Phases.Skip(1),
            phase => Assert.Equal(PhaseAvailability.Unavailable, phase.Availability));
    }

    [Fact]
    public async Task NewWorkflowDoesNotRetainAnotherWorkflowSession()
    {
        var first = new ChooseScopeWorkflow(
            new StubFolderPicker(@"C:\Photos"),
            new StubPreflight(ReadyEvidence(@"C:\Photos")));
        await first.HandleAsync(new ChooseScopeCommand(), CancellationToken.None);

        var restarted = new ChooseScopeWorkflow(
            new StubFolderPicker(null),
            new StubPreflight(ReadyEvidence(@"C:\Other")));

        Assert.Equal(ScopeSessionState.Cold, restarted.Current.ScopeState);
        Assert.Null(restarted.Current.Selection);
        Assert.Null(restarted.Current.Preflight);
        Assert.Equal(0, restarted.Current.Revision);
    }

    private static TaskCompletionSource<ScopePreflightEvidence> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static ScopePreflightEvidence BlockedEvidence(string path) =>
        ReadyEvidence(path) with { Volume = ScopeVolumeKind.Remote };

    private sealed class StubFolderPicker(string? path) : ILocalFolderPicker
    {
        public Task<string?> PickSingleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(path);
        }
    }

    private sealed class SequenceFolderPicker(params Task<string?>[] paths) : ILocalFolderPicker
    {
        private readonly Queue<Task<string?>> pending = new(paths);

        public SequenceFolderPicker(params string?[] paths)
            : this(paths.Select(Task.FromResult).ToArray())
        {
        }

        public Task<string?> PickSingleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return pending.Dequeue();
        }
    }

    private sealed class StubPreflight(ScopePreflightEvidence evidence) : ILocalScopePreflight
    {
        public Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(evidence);
        }
    }

    private sealed class SelectionPreflight : ILocalScopePreflight
    {
        public Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadyEvidence(
                selection.SelectedPath,
                selection.Recursion));
        }
    }

    private sealed class DelayedPreflight(
        params TaskCompletionSource<ScopePreflightEvidence>[] completions)
        : ILocalScopePreflight, IDisposable
    {
        private readonly TaskCompletionSource[] started = completions
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private int invocationCount;

        public Task WaitForStartAsync(int invocation) =>
            started[invocation - 1].Task.WaitAsync(
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        public Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref invocationCount) - 1;
            started[index].TrySetResult();
            return completions[index].Task;
        }

        public void Dispose()
        {
            foreach (var completion in completions)
            {
                completion.TrySetCanceled();
            }
        }
    }

    private sealed class ThrowingPreflight(Exception exception) : ILocalScopePreflight
    {
        public Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection,
            CancellationToken cancellationToken) =>
            Task.FromException<ScopePreflightEvidence>(exception);
    }
}
