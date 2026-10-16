using System.Collections.Concurrent;
using PhotoSift.App.Shell;
using PhotoSift.Application.Scope;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class ScopeOperationOrderingTests
{
    private static readonly TimeSpan FailureTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SupersededPickerAndWorkerCannotOverwriteTheLatestAcceptedScope()
    {
        const string path = @"C:\Photos";
        const string obsoletePath = @"C:\Obsolete";
        var picker = new DeferredReplacementPicker(path);
        var preflight = new DeferredInitialPreflight();
        var workflow = new ChooseScopeWorkflow(picker, preflight);
        var viewModel = new ShellViewModel(workflow);

        var initialOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
        Task? pickerOperation = null;
        try
        {
            await preflight.Started.Task.WaitAsync(
                FailureTimeout, TestContext.Current.CancellationToken);
            pickerOperation = viewModel.ChooseScopeCommand.ExecuteAsync();
            await picker.Requested.Task.WaitAsync(
                FailureTimeout, TestContext.Current.CancellationToken);

            await viewModel.ChangeRecursionCommand.ExecuteAsync(true).WaitAsync(
                FailureTimeout, TestContext.Current.CancellationToken);
            var accepted = workflow.Current;
            Assert.Equal(ScopeSessionState.Ready, accepted.ScopeState);
            Assert.Equal(path, accepted.Selection?.SelectedPath);
            Assert.Equal(ScopeRecursion.IncludeSubfolders, accepted.Selection?.Recursion);

            // Both external operations deliberately ignore cancellation. Correctness
            // must come from the accepting owner, not cooperation by a worker/dialog.
            preflight.Completion.SetResult(ReadyEvidence(
                new ScopeSelection(path, ScopeRecursion.TopLevelOnly)));
            await initialOperation.WaitAsync(
                FailureTimeout, TestContext.Current.CancellationToken);
            Assert.Same(accepted, workflow.Current);

            picker.Completion.SetResult(obsoletePath);
            await pickerOperation.WaitAsync(
                FailureTimeout, TestContext.Current.CancellationToken);

            Assert.Same(accepted, workflow.Current);
            Assert.True(viewModel.IsReady);
            Assert.Equal(path, viewModel.SelectedPath);
            Assert.Equal(path, viewModel.ResolvedPath);
            Assert.True(viewModel.IncludeSubfolders);
        }
        finally
        {
            preflight.Completion.TrySetResult(ReadyEvidence(
                new ScopeSelection(path, ScopeRecursion.TopLevelOnly)));
            picker.Completion.TrySetResult(null);
            await ObserveCleanupAsync(initialOperation, pickerOperation);
        }
    }

    [Fact]
    public async Task BackgroundRetryCannotBlockCompetingUiScopeCommandEntry()
    {
        using var ui = new QueuedUiContext();
        var viewModel = await ui.InvokeAsync(() => new ShellViewModel(
            new ChooseScopeWorkflow(new ImmediatePicker(), new ImmediatePreflight())))
            .WaitAsync(FailureTimeout, TestContext.Current.CancellationToken);
        await ui.InvokeAsync(() => viewModel.ChooseScopeCommand.ExecuteAsync())
            .Unwrap().WaitAsync(FailureTimeout, TestContext.Current.CancellationToken);

        var startWorker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiCommandReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? replacement = null;
        var worker = Task.Run(async () =>
        {
            await startWorker.Task;
            await viewModel.RetryScopeCommand.ExecuteAsync();
        }, TestContext.Current.CancellationToken);

        var competingUiTurn = ui.InvokeAsync(() =>
        {
            ui.ObserveCrossThreadDispatch();
            startWorker.SetResult();

            // Establish the reviewed schedule: the worker has reached the UI
            // dispatch boundary while the UI is still inside this command turn.
            ui.WaitForCrossThreadDispatch();
            replacement = viewModel.ChangeRecursionCommand.ExecuteAsync(true);
            uiCommandReturned.SetResult();
            return true;
        });

        try
        {
            // A timeout bounds a genuine circular wait. It does not arrange the
            // race: the explicit dispatch handshake above fixes that ordering.
            await uiCommandReturned.Task.WaitAsync(
                FailureTimeout, TestContext.Current.CancellationToken);
            await Task.WhenAll(worker, competingUiTurn,
                Assert.IsAssignableFrom<Task>(replacement)).WaitAsync(
                    FailureTimeout, TestContext.Current.CancellationToken);

            var projected = await ui.InvokeAsync(() =>
                (viewModel.IsReady, viewModel.IncludeSubfolders, viewModel.SelectedPath))
                .WaitAsync(FailureTimeout, TestContext.Current.CancellationToken);
            Assert.True(projected.IsReady);
            Assert.True(projected.IncludeSubfolders);
            Assert.Equal(@"C:\Photos", projected.SelectedPath);
            Assert.Equal(0, ui.CrossThreadSendCount);
        }
        finally
        {
            // Failed old implementations must not leave blocked fixture threads
            // behind. Abort Send's wait so it unwinds and releases the old gate.
            startWorker.TrySetResult();
            ui.AbortBlockedSends();
            await ObserveCleanupAsync(worker, competingUiTurn, replacement);
        }
    }

    private static async Task ObserveCleanupAsync(params Task?[] operations)
    {
        try
        {
            await Task.WhenAll(operations.OfType<Task>()).WaitAsync(FailureTimeout);
        }
        catch (Exception)
        {
            // Preserve the scenario's original failure while observing task faults.
        }
    }

    private static ScopePreflightEvidence ReadyEvidence(ScopeSelection selection) =>
        new(
            new ResolvedScope(selection, selection.SelectedPath, new DirectoryIdentity(1, 0, 1)),
            ScopeVolumeKind.Fixed,
            ScopeCloudState.Local,
            ScopeTraversalState.Complete,
            new ScopeOutcomeCounts(1, 0, 0, 0),
            new ScopeRestrictionCounts(0, 0));

    private sealed class DeferredReplacementPicker(string initialPath) : ILocalFolderPicker
    {
        private int invocationCount;

        public TaskCompletionSource Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> PickSingleAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                return Task.FromResult<string?>(initialPath);
            }

            Requested.SetResult();
            return Completion.Task;
        }
    }

    private sealed class DeferredInitialPreflight : ILocalScopePreflight
    {
        private int invocationCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ScopePreflightEvidence> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                Started.SetResult();
                return Completion.Task;
            }

            return Task.FromResult(ReadyEvidence(selection));
        }
    }

    private sealed class ImmediatePicker : ILocalFolderPicker
    {
        public Task<string?> PickSingleAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(@"C:\Photos");
    }

    private sealed class ImmediatePreflight : ILocalScopePreflight
    {
        public Task<ScopePreflightEvidence> InspectAsync(
            ScopeSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult(ReadyEvidence(selection));
    }

    private sealed class QueuedUiContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> callbacks = new();
        private readonly CancellationTokenSource abortSends = new();
        private readonly ManualResetEventSlim dispatchRequested = new();
        private readonly Thread thread;
        private int observingDispatch;
        private int crossThreadSendCount;

        public QueuedUiContext()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Scope scenario UI" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public int CrossThreadSendCount => Volatile.Read(ref crossThreadSendCount);

        public Task<T> InvokeAsync<T>(Func<T> callback)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            callbacks.Add(() =>
            {
                try
                {
                    completion.SetResult(callback());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
            return completion.Task;
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            callbacks.Add(() => callback(state));
            SignalCrossThreadDispatch();
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (ReferenceEquals(Current, this))
            {
                callback(state);
                return;
            }

            Interlocked.Increment(ref crossThreadSendCount);
            var completion = InvokeAsync(() =>
            {
                callback(state);
                return true;
            });
            SignalCrossThreadDispatch();
            completion.WaitAsync(abortSends.Token).GetAwaiter().GetResult();
        }

        public void ObserveCrossThreadDispatch() => Volatile.Write(ref observingDispatch, 1);

        public void WaitForCrossThreadDispatch()
        {
            if (!dispatchRequested.Wait(FailureTimeout))
            {
                throw new TimeoutException("The worker did not reach the UI dispatch boundary.");
            }
        }

        public void AbortBlockedSends() => abortSends.Cancel();

        public void Dispose()
        {
            abortSends.Cancel();
            callbacks.CompleteAdding();
            if (!thread.Join(FailureTimeout))
            {
                throw new TimeoutException("The scenario UI thread did not stop after cleanup.");
            }

            callbacks.Dispose();
            dispatchRequested.Dispose();
            abortSends.Dispose();
        }

        private void SignalCrossThreadDispatch()
        {
            if (Volatile.Read(ref observingDispatch) != 0 && !ReferenceEquals(Current, this))
            {
                dispatchRequested.Set();
            }
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var callback in callbacks.GetConsumingEnumerable())
            {
                callback();
            }
        }
    }
}
