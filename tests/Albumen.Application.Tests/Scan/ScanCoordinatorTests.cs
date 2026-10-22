using System.Collections.Concurrent;
using Albumen.Application.Scan;
using Albumen.Application.Scope;
using Albumen.Application.Semantic;
using Albumen.Application.Shell;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ScanCoordinatorTests
{
    [Fact]
    public async Task InvalidOptionsAreRejectedBeforeScanAdmission()
    {
        await using var fixture = new CoordinatorFixture();
        var invalid = new ExactRuleConfirmation(1, 1,
            new(ExactDuplicateMode.Disabled, NearDuplicateMode.Conservative));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Pump(fixture.Owner.StartAsync(fixture.Ready(), invalid, CancellationToken.None)));

        Assert.Equal(ScanStatus.Idle, fixture.Owner.Current.Status);
        Assert.Empty(fixture.Executor.Started);
    }

    [Theory]
    [InlineData(NearDuplicateMode.Conservative, 1)]
    [InlineData(NearDuplicateMode.Disabled, 0)]
    public async Task PinnedNearModeControlsOnlyTheExistingNearPipeline(
        NearDuplicateMode nearMode, int expectedCalls)
    {
        var near = new RecordingNearPipeline();
        await using var fixture = new CoordinatorFixture(nearDuplicates: near);
        var options = new ScanOptions(ExactDuplicateMode.Enabled, nearMode);

        await fixture.Pump(fixture.Owner.StartAsync(fixture.Ready(),
            new ExactRuleConfirmation(1, 1, options), CancellationToken.None));
        var run = await fixture.Run();
        await fixture.Pump(fixture.Owner.EnumerationEndedAsync(
            run.Context,
            ScanTestData.Success).AsTask());
        run.Done.SetResult();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        Assert.Equal(options, run.Context.Options);
        Assert.Equal(expectedCalls, near.Calls);
    }

    [Fact]
    public async Task StartAcceptsCheckingBeforePreflightAndCompletionRequiresRunSettlement()
    {
        await using var fixture = new CoordinatorFixture();
        var inspection = ScanTestData.Signal<ScopePreflightEvidence>();
        fixture.Preflight.Inspect = (_, _) => inspection.Task;
        await fixture.Start();
        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
        Assert.Empty(fixture.Executor.Started);
        inspection.SetResult(fixture.Evidence);
        var run = await fixture.Run();
        await fixture.Pump(fixture.Owner.EnumerationEndedAsync(run.Context, ScanTestData.Success).AsTask());
        Assert.Equal(ScanStatus.Running, fixture.Owner.Current.Status);
        run.Done.SetResult();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);
        Assert.Equal(new ScanCounts(0, 0, 0, 0, 0, 0), fixture.Owner.Current.Counts);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("path")]
    [InlineData("cloud")]
    public async Task FreshPreflightMustRevalidateApprovedRoot(string mismatch)
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Preflight.Inspect = (_, _) => Task.FromResult(mismatch switch
        {
            "identity" => fixture.Evidence with { Scope = fixture.Scope with { RootIdentity = new DirectoryIdentity(9, 9, 9) } },
            "path" => fixture.Evidence with { Scope = fixture.Scope with { ResolvedPath = @"C:\Other" } },
            _ => fixture.Evidence with { Cloud = ScopeCloudState.Unknown },
        });
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Incomplete);
        Assert.Empty(fixture.Executor.Started);
    }

    [Fact]
    public async Task LeaseRemainsAliveDuringOwnerAcceptanceAndAcknowledgment()
    {
        var reader = new ScanTestReader();
        var analyzer = new ScanTestAnalyzer();
        await using var fixture = new CoordinatorFixture(new BoundedScanExecutor(new ScanTestEnumerator(1), reader, analyzer));
        var observed = false;
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (!observed && snapshot.Counts.Analyzed == 1 && snapshot.Status == ScanStatus.Running)
            { Assert.Equal(1, reader.Live); observed = true; }
        };
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);
        Assert.True(observed);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task ObserverReentryQueuesAnotherTurnAndCannotNestPublication()
    {
        await using var fixture = new CoordinatorFixture();
        Task? cancellation = null;
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status != ScanStatus.Checking) return;
            cancellation = fixture.Owner.CancelAsync();
            Assert.False(fixture.Scheduler.TryRunOne());
            Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
        };
        await fixture.Start();
        await fixture.Pump(cancellation!);
        Assert.Equal(ScanStatus.Incomplete, fixture.Owner.Current.Status);
    }

    [Fact]
    public async Task ObserverFaultIsObservableAndCannotLeaveCheckingOrAnUnsettledCommand()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        { if (snapshot.Status == ScanStatus.Checking) throw new InvalidOperationException("observer fault"); };
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Faulted);
        await fixture.Pump(fixture.Owner.DisposeAsync().AsTask());
        Assert.Equal(ScanStatus.Faulted, fixture.Owner.Current.Status);
        Assert.Equal(ScanReason.ProgrammingFault, fixture.Owner.Current.Enumeration.Reason);
    }

    [Fact]
    public async Task RunningObserverFaultRequestsCancellationBeforeDisposalAndDrainsAcknowledgments()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        var first = fixture.Owner.RegisterAsync(run.Context, ScanTestData.Work(1, run.Context).File).AsTask();
        await fixture.Pump(first);
        var work = Assert.IsType<OccurrenceWork>(await first);
        var second = fixture.Owner.RegisterAsync(run.Context, ScanTestData.Work(2, run.Context).File).AsTask();
        await fixture.Pump(second);
        var outstanding = Assert.IsType<OccurrenceWork>(await second);
        var cancellationRequested = ScanTestData.Signal<ScanStatus>();
        var rejection = ScanTestData.Signal<bool>();
        run.OnCancel = async () =>
        {
            cancellationRequested.TrySetResult(fixture.Owner.Current.Status);
            rejection.TrySetResult(await fixture.Owner.FailAsync(outstanding.Token, ScanTestData.Canceled));
        };
        var throwsRemaining = 1;
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == ScanStatus.Running && snapshot.Counts.Analyzed == 1 && throwsRemaining-- > 0)
                throw new InvalidOperationException("one-shot observer failure after signature acceptance");
        };
        var signature = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(work)).AsTask();
        await fixture.Pump(signature);
        Assert.True((await signature).Accepted);
        Assert.Equal(ScanStatus.Faulted, fixture.Owner.Current.Status);
        Assert.True(fixture.Owner.Current.HasUnsettledWork);

        // The fake run's Completion can only settle when CancelAsync is called.
        // Dispose is deliberately not a trigger. The timeout bounds a missing
        // cancellation signal, then gives a contract assertion rather than a hang.
        try { await fixture.Pump(cancellationRequested.Task); }
        catch (TimeoutException) { }
        Assert.True(cancellationRequested.Task.IsCompletedSuccessfully,
            "A Running observer fault must request executor cancellation before waiting for Completion or disposal.");
        Assert.Equal(ScanStatus.Faulted, await cancellationRequested.Task);
        await fixture.Pump(rejection.Task);
        Assert.False(await rejection.Task);
        await fixture.Pump(run.Disposed.Task);
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Faulted && snapshot.Counts.Outstanding == 0);
        Assert.False(fixture.Owner.Current.HasUnsettledWork);
        Assert.Equal(1, fixture.Owner.Current.Counts.Analyzed);
        Assert.Equal(1, fixture.Owner.Current.Counts.Failed);
        Assert.Equal(ScanReason.ProgrammingFault, fixture.Owner.Current.Enumeration.Reason);
    }

    [Fact]
    public async Task RunAndDisposalFaultsPublishFaulted()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        run.DisposalFailure = new InvalidOperationException("callback failure during disposal");
        run.Done.SetResult();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Faulted);
        Assert.Equal(ScanReason.ProgrammingFault, fixture.Owner.Current.Enumeration.Reason);
    }

    [Fact]
    public async Task CancellationReconcilesUnstartedComparisonTicketsAndPreservesSignatures()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        async Task<SignatureCommit> Sign(long id)
        {
            var registration = fixture.Owner.RegisterAsync(run.Context, ScanTestData.Work(id, run.Context).File).AsTask();
            await fixture.Pump(registration);
            var work = Assert.IsType<OccurrenceWork>(await registration);
            var commit = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(work)).AsTask();
            await fixture.Pump(commit);
            return await commit;
        }
        await Sign(1);
        var second = Assert.Single((await Sign(2)).Comparisons);
        await fixture.Pump(fixture.Owner.CommitComparisonAsync(StoreFixture.Equality(second, false)).AsTask());
        var pending = (await Sign(3)).Comparisons;
        Assert.Equal(2, pending.Length);
        await fixture.Pump(fixture.Owner.FailAsync(pending[0].Right.Token, ScanTestData.Canceled).AsTask());
        await fixture.Pump(fixture.Owner.CancelAsync());
        run.Done.TrySetResult();
        await fixture.Pump(run.Disposed.Task);
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Incomplete);
        Assert.Equal(ScanStatus.Incomplete, fixture.Owner.Current.Status);
        Assert.Equal(3, fixture.Owner.Current.Counts.Analyzed);
        Assert.Equal(0, fixture.Owner.Current.Counts.Outstanding);
        foreach (var descriptor in pending)
        {
            var late = fixture.Owner.CommitComparisonAsync(StoreFixture.Equality(descriptor)).AsTask();
            await fixture.Pump(late);
            Assert.False(await late);
        }
    }
}

internal sealed class CoordinatorFixture : IAsyncDisposable
{
    public ManualScanScheduler Scheduler { get; } = new();
    public CoordinatorPreflight Preflight { get; } = new();
    public CoordinatorExecutor Executor { get; } = new();
    public CoordinatorIdentityStore Identities { get; }
    public CoordinatorCheckpointStore Checkpoints { get; }
    public ScanCoordinator Owner { get; }
    public ResolvedScope Scope { get; } = ScanTestData.Context().Scope;
    public ScopePreflightEvidence Evidence => new(Scope, ScopeVolumeKind.Fixed, ScopeCloudState.Local,
        ScopeTraversalState.Complete, new ScopeOutcomeCounts(1, 0, 0, 0), new ScopeRestrictionCounts(0, 0));
    public ShellSnapshot Ready(long revision = 1) => new([], "ready", ScopeSessionState.Ready,
        Scope.Selection, ScopePreflightPolicy.Evaluate(Evidence), Revision: revision);
    public CoordinatorFixture(
        IScanExecutor? executor = null,
        CoordinatorIdentityStore? identities = null,
        CoordinatorCheckpointStore? checkpoints = null,
        INearDuplicatePipeline? nearDuplicates = null,
        ISemanticIndexer? semanticIndex = null,
        IEmbeddingMatcher? embeddingMatches = null)
    {
        Identities = identities ?? new CoordinatorIdentityStore();
        Checkpoints = checkpoints ?? new CoordinatorCheckpointStore();
        Preflight.Inspect = (_, _) => Task.FromResult(Evidence);
        Owner = new ScanCoordinator(
            Scheduler,
            Preflight,
            executor ?? Executor,
            Identities,
            Checkpoints,
            nearDuplicates,
            semanticIndex,
            embeddingMatches);
    }
    public Task Pump(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));
    public Task Start(long scope = 1, long rules = 1) => Pump(Owner.StartAsync(Ready(scope), new ExactRuleConfirmation(scope, rules), CancellationToken.None));
    public async Task<CoordinatorRun> Run()
    {
        await Pump(Executor.Arrived.Task);
        return await Executor.Arrived.Task;
    }
    public async Task Until(Func<ScanSnapshot, bool> condition)
    {
        var reached = ScanTestData.Signal<bool>();
        void Handler(object? sender, ScanSnapshot snapshot) { if (condition(snapshot)) reached.TrySetResult(true); }
        Owner.SnapshotChanged += Handler;
        try
        {
            if (condition(Owner.Current)) reached.TrySetResult(true);
            await Pump(reached.Task);
        }
        finally { Owner.SnapshotChanged -= Handler; }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var run in Executor.Started) run.Done.TrySetResult();
        await Pump(Owner.DisposeAsync().AsTask());
    }
}

internal sealed class RecordingNearPipeline : INearDuplicatePipeline
{
    public int Calls { get; private set; }

    public Task<NearDuplicateBatch> AnalyzeAsync(
        ScanRunContext context,
        System.Collections.Immutable.ImmutableArray<ScanFileOccurrence> rows,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new NearDuplicateBatch(
            context.Pin,
            BoundedNearDuplicatePipeline.ImplementationRevision,
            []));
    }
}

internal sealed class CoordinatorPreflight : ILocalScopePreflight
{
    public Func<ScopeSelection, CancellationToken, Task<ScopePreflightEvidence>> Inspect { get; set; } = null!;
    public Task<ScopePreflightEvidence> InspectAsync(ScopeSelection selection, CancellationToken cancellationToken) => Inspect(selection, cancellationToken);
}

internal sealed class CoordinatorExecutor : IScanExecutor
{
    public ConcurrentQueue<CoordinatorRun> Started { get; } = new();
    public TaskCompletionSource<CoordinatorRun> Arrived { get; set; } = ScanTestData.Signal<CoordinatorRun>();
    public IScanRun Start(ScanRunContext context, IScanResultSink sink)
    {
        var run = new CoordinatorRun(context);
        Started.Enqueue(run);
        Arrived.TrySetResult(run);
        return run;
    }
}

internal sealed class CoordinatorRun(ScanRunContext context) : IScanRun
{
    public ScanRunContext Context { get; } = context;
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<Task>? OnCancel { get; set; }
    public Exception? DisposalFailure { get; set; }
    public TaskCompletionSource CancellationRequested { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CancellationRequestCount;
    public Task Completion => Done.Task;
    public Task CancelAsync()
    {
        Interlocked.Increment(ref CancellationRequestCount);
        CancellationRequested.TrySetResult();
        Done.TrySetResult();
        return OnCancel?.Invoke() ?? Task.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        Disposed.TrySetResult();
        return DisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalFailure);
    }
}
