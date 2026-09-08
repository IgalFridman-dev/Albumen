using PhotoSift.Application.Scope;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Scan;

public sealed class ScanCoordinator : IScanCoordinator, IScanResultSink
{
    private static readonly StageOutcome Pending = new(ScanStageState.Pending, ScanReason.None);
    private static readonly StageOutcome Running = new(ScanStageState.Running, ScanReason.None);
    private static readonly StageOutcome Canceled = new(ScanStageState.Canceled, ScanReason.Canceled);
    private static readonly StageOutcome IncompleteEnumeration = new(ScanStageState.FailedTerminal, ScanReason.IncompleteEnumeration);
    private static readonly StageOutcome ProgrammingFault = new(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault);
    private readonly ScanOwnerInbox inbox;
    private readonly ILocalScopePreflight preflight;
    private readonly IScanExecutor executor;
    private readonly HashSet<Operation> operations = [];
    private Operation? active;
    private ScanSnapshot current = ScanSnapshotProjector.Create(null, ScanStatus.Idle, Pending, [], []);
    private long scopeWatermark;
    private long ruleWatermark;
    private long revokedRuleWatermark;
    private bool disposed;

    public ScanCoordinator(IWorkflowScheduler scheduler, ILocalScopePreflight preflight, IScanExecutor executor)
    {
        inbox = new ScanOwnerInbox(scheduler);
        this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public ScanSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<ScanSnapshot>? SnapshotChanged;

    public async Task StartAsync(ShellSnapshot scope, ExactRuleConfirmation rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rules);
        var transition = await inbox.CommandAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.ScopeState != ScopeSessionState.Ready || scope.Preflight is not { Decision: ScopePreflightDecision.Ready, Scope: { } resolved }
                || scope.Selection != resolved.Selection || rules.ScopeRevision != scope.Revision || rules.RuleSetRevision <= 0)
                throw new InvalidOperationException("A Ready Scope and its confirmed Exact rules are required.");
            if (scope.Revision < scopeWatermark || rules.RuleSetRevision < ruleWatermark
                || rules.RuleSetRevision <= revokedRuleWatermark) return (New: (Operation?)null, Old: (Operation?)null);
            scopeWatermark = scope.Revision;
            ruleWatermark = rules.RuleSetRevision;
            var previous = active;
            var context = new ScanRunContext(new ScanPin(Guid.NewGuid(), scope.Revision, rules.RuleSetRevision, 1), resolved);
            var operation = new Operation(context);
            active = operation;
            operations.Add(operation);
            Publish(operation.Store.Snapshot(ScanStatus.Checking, Pending));
            return (New: (Operation?)operation, Old: previous);
        }).ConfigureAwait(false);

        // Every effect, including cancellation registration, runs after acceptance.
        // Background launch also keeps synchronous native preflight out of callers.
        if (transition.Old is { } old) _ = old.RequestCancellation();
        if (transition.New is { } next)
        {
            next.CallerCancellation = cancellationToken.Register(() => { _ = CancelOperationAsync(next); });
            _ = Task.Run(() => RunOperationAsync(next), CancellationToken.None);
        }
    }

    public async Task InvalidateAsync(long scopeRevision, long? ruleSetRevision)
    {
        var previous = await inbox.CommandAsync(() =>
        {
            if (scopeRevision < scopeWatermark) return null;
            var advanced = scopeRevision > scopeWatermark || ruleSetRevision is null || ruleSetRevision > ruleWatermark;
            scopeWatermark = Math.Max(scopeWatermark, scopeRevision);
            if (ruleSetRevision is { } rules) ruleWatermark = Math.Max(ruleWatermark, rules);
            else revokedRuleWatermark = Math.Max(revokedRuleWatermark, ruleWatermark);
            if (!advanced || active is null) return null;
            var old = active;
            active = null;
            Publish(ScanSnapshotProjector.Create(null, ScanStatus.Idle, Pending, [], []));
            return old;
        }).ConfigureAwait(false);
        if (previous is not null) _ = previous.RequestCancellation();
    }

    public async Task CancelAsync()
    {
        var operation = await inbox.CommandAsync(() =>
        {
            if (active is not { } operation) return null;
            AcceptCancellation(operation);
            return operation;
        }).ConfigureAwait(false);
        if (operation is not null) _ = operation.RequestCancellation();
    }

    private async Task CancelOperationAsync(Operation operation)
    {
        await inbox.CommandAsync(() =>
        {
            if (ReferenceEquals(active, operation)) AcceptCancellation(operation);
            return true;
        }).ConfigureAwait(false);
        _ = operation.RequestCancellation();
    }

    private void AcceptCancellation(Operation operation)
    {
        operation.Canceled = true;
        operation.Store.Reconcile(Canceled);
        if (current.Status != ScanStatus.Faulted) Publish(operation.Store.Snapshot(ScanStatus.Incomplete, Canceled));
    }

    public async ValueTask<OccurrenceWork?> RegisterAsync(ScanRunContext context, EnumeratedFile file) =>
        await inbox.ResultAsync(() =>
        {
            if (!CanAccept(context.Pin) || active!.Context != context) return null;
            var result = active.Store.Register(file);
            PublishResults(active);
            return result;
        }).ConfigureAwait(false);

    public async ValueTask<SignatureCommit> CommitSignatureAsync(SignatureEvidence evidence) =>
        await inbox.ResultAsync(() =>
        {
            if (!CanAccept(evidence.Token.Pin)) return new SignatureCommit(false, []);
            var result = active!.Store.CommitSignature(evidence);
            PublishResults(active);
            return result;
        }).ConfigureAwait(false);

    public async ValueTask<bool> CommitComparisonAsync(ComparisonEvidence evidence) =>
        await inbox.ResultAsync(() =>
        {
            if (!CanAccept(evidence.Work.Left.Token.Pin)) return false;
            var result = active!.Store.CommitComparison(evidence);
            PublishResults(active);
            return result;
        }).ConfigureAwait(false);

    public async ValueTask<bool> FailAsync(OccurrenceToken token, StageOutcome outcome) =>
        await inbox.ResultAsync(() =>
        {
            if (!CanAccept(token.Pin)) return false;
            var result = active!.Store.Fail(token, outcome);
            PublishResults(active);
            return result;
        }).ConfigureAwait(false);

    public async ValueTask EnumerationEndedAsync(ScanRunContext context, StageOutcome outcome)
    {
        await inbox.ResultAsync(() =>
        {
            if (!CanAccept(context.Pin) || active!.Context != context || active.Enumeration.State != ScanStageState.Running) return false;
            active.Enumeration = outcome;
            PublishResults(active);
            return true;
        }).ConfigureAwait(false);
    }

    private bool CanAccept(ScanPin pin) => !disposed && active is { Canceled: false, Finished: false, Faulted: false }
        && active.Context.Pin == pin && current.Status == ScanStatus.Running;

    private async Task RunOperationAsync(Operation operation)
    {
        IScanRun? run = null;
        var faulted = false;
        try
        {
            var inspect = await inbox.CommandAsync(() => ReferenceEquals(active, operation)
                && !operation.Canceled && current.Status == ScanStatus.Checking).ConfigureAwait(false);
            if (!inspect) return;
            var evidence = await preflight.InspectAsync(operation.Context.Scope.Selection, operation.Token).ConfigureAwait(false);
            var start = await inbox.CommandAsync(() =>
            {
                if (!ReferenceEquals(active, operation) || operation.Canceled || current.Status != ScanStatus.Checking) return false;
                var result = ScopePreflightPolicy.Evaluate(evidence);
                if (result.Decision != ScopePreflightDecision.Ready || result.Scope is not { } scope
                    || scope.RootIdentity != operation.Context.Scope.RootIdentity
                    || scope.Selection != operation.Context.Scope.Selection
                    || !SamePath(scope.ResolvedPath, operation.Context.Scope.ResolvedPath))
                {
                    operation.Enumeration = new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety);
                    Publish(operation.Store.Snapshot(ScanStatus.Incomplete, operation.Enumeration));
                    return false;
                }
                operation.Enumeration = Running;
                Publish(operation.Store.Snapshot(ScanStatus.Running, Running));
                return current.Status == ScanStatus.Running;
            }).ConfigureAwait(false);
            if (!start) return;
            operation.Token.ThrowIfCancellationRequested();
            run = executor.Start(operation.Context, this);
            operation.RunReady.TrySetResult(run);
            await run.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
        catch (Exception)
        {
            faulted = true;
            await inbox.CommandAsync(() =>
            {
                if (ReferenceEquals(active, operation))
                    Publish(operation.Store.Snapshot(ScanStatus.Faulted, ProgrammingFault));
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            operation.RunReady.TrySetResult(run);
            // Completion and callbacks are separate obligations. Both are observed
            // before resource disposal and before terminal success can be published.
            try
            {
                var callbacks = operation.RequestCancellation();
                var disposal = run is null ? Task.CompletedTask : run.DisposeAsync().AsTask();
                await Task.WhenAll(callbacks, disposal).ConfigureAwait(false);
            }
            catch (Exception) { faulted = true; }
            await operation.DisposeAsync().ConfigureAwait(false);
            await inbox.CommandAsync(() =>
            {
                operation.Finished = true;
                if (ReferenceEquals(active, operation))
                {
                    if (faulted || operation.Faulted || operation.Store.Faulted || current.Status == ScanStatus.Faulted)
                    {
                        operation.Store.Reconcile(ProgrammingFault);
                        operation.Enumeration = ProgrammingFault;
                        Publish(operation.Store.Snapshot(ScanStatus.Faulted, ProgrammingFault));
                    }
                    else
                    {
                        operation.Store.Reconcile(operation.Canceled ? Canceled : IncompleteEnumeration);
                        if (operation.Enumeration.State is ScanStageState.Pending or ScanStageState.Running)
                            operation.Enumeration = IncompleteEnumeration;
                        var complete = !operation.Canceled && !operation.Store.Incomplete
                            && operation.Enumeration.State == ScanStageState.Succeeded && operation.Store.PendingComparisons == 0;
                        Publish(operation.Store.Snapshot(complete ? ScanStatus.Complete : ScanStatus.Incomplete,
                            operation.Canceled ? Canceled : operation.Enumeration));
                    }
                }
                operations.Remove(operation);
                operation.Settled.TrySetResult();
                return true;
            }).ConfigureAwait(false);
        }
    }

    private void PublishResults(Operation operation)
    {
        var fault = operation.Faulted || operation.Store.Faulted || operation.Enumeration.Reason == ScanReason.ProgrammingFault;
        Publish(operation.Store.Snapshot(fault ? ScanStatus.Faulted : ScanStatus.Running,
            fault ? ProgrammingFault : operation.Enumeration));
    }

    private void Publish(ScanSnapshot snapshot)
    {
        Operation? newlyFaulted = null;
        if (snapshot.Status == ScanStatus.Faulted && active is { } operation && snapshot.Pin == operation.Context.Pin)
        {
            if (!operation.Faulted) newlyFaulted = operation;
            operation.Faulted = true;
        }
        Volatile.Write(ref current, snapshot);
        if (newlyFaulted is not null) _ = CancelFaultedOperationAsync(newlyFaulted);
        var observers = SnapshotChanged?.GetInvocationList();
        if (observers is null) return;
        var failed = false;
        foreach (var observer in observers)
        {
            try { ((EventHandler<ScanSnapshot>)observer)(this, snapshot); }
            catch (Exception) { failed = true; }
        }
        if (!failed || snapshot.Status == ScanStatus.Faulted) return;
        // A throwing observer cannot prevent acknowledgment or hide the fault.
        Publish(snapshot with { Status = ScanStatus.Faulted, Enumeration = ProgrammingFault });
    }

    private async Task CancelFaultedOperationAsync(Operation operation)
    {
        // Crossing another non-reentrant turn ensures that fault publication is
        // finished before cancellation effects run. No worker/callback wait can
        // hold the owner; admitted result acknowledgments keep draining.
        var cancel = await inbox.CommandAsync(() => ReferenceEquals(active, operation)
            && operation.Faulted).ConfigureAwait(false);
        if (!cancel) return;
        try { await operation.RequestCancellation().ConfigureAwait(false); }
        catch (Exception)
        {
            await inbox.CommandAsync(() =>
            {
                if (ReferenceEquals(active, operation))
                    Publish(operation.Store.Snapshot(ScanStatus.Faulted, ProgrammingFault));
                return true;
            }).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var pending = await inbox.CommandAsync(() =>
        {
            disposed = true;
            if (active is { } operation) AcceptCancellation(operation);
            return operations.ToArray();
        }).ConfigureAwait(false);
        foreach (var operation in pending) _ = operation.RequestCancellation();
        await Task.WhenAll(pending.Select(operation => operation.Settled.Task)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static bool SamePath(string left, string right) =>
        ScopePathContainment.Contains(left, right) && ScopePathContainment.Contains(right, left);

    // Lifecycle synchronization protects cancellation-resource ownership only.
    // Accepted state above is exclusively mutated by ScanOwnerInbox turns.
    private sealed class Operation : IAsyncDisposable
    {
        private readonly CancellationTokenSource source = new();
        private readonly object lifecycle = new();
        private Task? cancellation;
        private bool resourcesDisposed;
        public Operation(ScanRunContext context)
        {
            Context = context;
            Store = new ScanStore(context);
            Token = source.Token;
        }
        public ScanRunContext Context { get; }
        public ScanStore Store { get; }
        public CancellationToken Token { get; }
        public CancellationTokenRegistration CallerCancellation { get; set; }
        public TaskCompletionSource<IScanRun?> RunReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StageOutcome Enumeration { get; set; } = Pending;
        public bool Canceled { get; set; }
        public bool Finished { get; set; }
        public bool Faulted { get; set; }
        public Task RequestCancellation()
        {
            lock (lifecycle)
            {
                if (resourcesDisposed) return cancellation ?? Task.CompletedTask;
                return cancellation ??= Task.Run(async () =>
                {
                    var callbacks = source.CancelAsync();
                    var runCancellation = CancelRunAsync();
                    await Task.WhenAll(callbacks, runCancellation).ConfigureAwait(false);
                });
            }
        }
        private async Task CancelRunAsync()
        {
            var run = await RunReady.Task.ConfigureAwait(false);
            if (run is not null) await run.CancelAsync().ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            await CallerCancellation.DisposeAsync().ConfigureAwait(false);
            lock (lifecycle) { resourcesDisposed = true; source.Dispose(); }
        }
    }
}
