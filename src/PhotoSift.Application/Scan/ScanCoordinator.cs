using System.Threading.Channels;
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
    private static readonly StageOutcome IncompleteEnumeration = new(
        ScanStageState.FailedTerminal, ScanReason.IncompleteEnumeration);
    private static readonly StageOutcome ProgrammingFault = new(
        ScanStageState.FailedTerminal, ScanReason.ProgrammingFault);
    private readonly ScanOwnerInbox inbox;
    private readonly CheckpointGate checkpointGate = new();
    private readonly ILocalScopePreflight preflight;
    private readonly IScanExecutor executor;
    private readonly IScanIdentityStore identities;
    private readonly IScanCheckpointStore checkpoints;
    private readonly INearDuplicatePipeline? nearDuplicates;
    private readonly HashSet<Operation> operations = [];
    private Operation? active;
    private ScanSnapshot current = ScanSnapshotProjector.Create(null, ScanStatus.Idle, Pending, [], []);
    private long scopeWatermark;
    private long ruleWatermark;
    private long revokedRuleWatermark;
    private bool disposed;

    public ScanCoordinator(
        IWorkflowScheduler scheduler,
        ILocalScopePreflight preflight,
        IScanExecutor executor,
        IScanIdentityStore identities,
        IScanCheckpointStore checkpoints,
        INearDuplicatePipeline? nearDuplicates = null)
    {
        inbox = new ScanOwnerInbox(scheduler);
        this.preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
        this.checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        this.nearDuplicates = nearDuplicates;
    }

    public ScanSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<ScanSnapshot>? SnapshotChanged;

    public async Task StartAsync(
        ShellSnapshot scope,
        ExactRuleConfirmation rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rules);
        var transition = await inbox.CommandAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.ScopeState != ScopeSessionState.Ready
                || scope.Preflight is not { Decision: ScopePreflightDecision.Ready, Scope: { } resolved }
                || scope.Selection != resolved.Selection
                || rules.ScopeRevision != scope.Revision
                || rules.RuleSetRevision <= 0
                || !rules.Options.IsValid)
            {
                throw new InvalidOperationException(
                    "A Ready Scope and valid confirmed Scan options are required.");
            }
            if (scope.Revision < scopeWatermark
                || rules.RuleSetRevision < ruleWatermark
                || rules.RuleSetRevision <= revokedRuleWatermark)
            {
                return (New: (Operation?)null, Old: (Operation?)null);
            }
            scopeWatermark = scope.Revision;
            ruleWatermark = rules.RuleSetRevision;
            var previous = active;
            var context = new ScanRunContext(
                new ScanPin(Guid.NewGuid(), scope.Revision, rules.RuleSetRevision, 1), resolved)
            {
                Options = rules.Options,
            };
            var operation = new Operation(context);
            active = operation;
            operations.Add(operation);
            Publish(operation.Store.Snapshot(ScanStatus.Checking, Pending));
            return (New: (Operation?)operation, Old: previous);
        }).ConfigureAwait(false);

        // Every effect, including cancellation registration, runs after acceptance.
        // One background launch keeps synchronous native preflight out of callers.
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
            var advanced = scopeRevision > scopeWatermark
                || ruleSetRevision is null
                || ruleSetRevision > ruleWatermark;
            scopeWatermark = Math.Max(scopeWatermark, scopeRevision);
            if (ruleSetRevision is { } rules) ruleWatermark = Math.Max(ruleWatermark, rules);
            else revokedRuleWatermark = Math.Max(revokedRuleWatermark, ruleWatermark);
            if (!advanced || active is null) return null;
            var old = active;
            active = null;
            old.Canceled = true;
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
        // A durable Running Scan publishes its terminal state only from finalization,
        // after the checkpoint gate has drained every earlier admission.
        if (!operation.DurableStarted && current.Status != ScanStatus.Faulted)
            Publish(operation.Store.Snapshot(ScanStatus.Incomplete, Canceled));
    }

    public ValueTask<OccurrenceWork?> RegisterAsync(ScanRunContext context, EnumeratedFile file) =>
        new(inbox.ResultOperationAsync(() =>
        {
            if (!CanAccept(context.Pin) || active!.Context != context)
                return Task.FromResult<OccurrenceWork?>(null);
            var operation = active!;
            return checkpointGate.ExecuteAsync(() => CommitRegistrationAsync(operation, file));
        }));

    public ValueTask<SignatureCommit> CommitSignatureAsync(SignatureEvidence evidence) =>
        new(inbox.ResultOperationAsync(() =>
        {
            if (!CanAccept(evidence.Token.Pin))
                return Task.FromResult(new SignatureCommit(false, []));
            var operation = active!;
            return checkpointGate.ExecuteAsync(() => CommitSignatureCoreAsync(operation, evidence));
        }));

    public ValueTask<bool> CommitComparisonAsync(ComparisonEvidence evidence) =>
        new(inbox.ResultOperationAsync(() =>
        {
            if (!CanAccept(evidence.Work.Left.Token.Pin)) return Task.FromResult(false);
            var operation = active!;
            return checkpointGate.ExecuteAsync(() => CommitComparisonCoreAsync(operation, evidence));
        }));

    public ValueTask<bool> FailAsync(OccurrenceToken token, StageOutcome outcome) =>
        new(inbox.ResultOperationAsync(() =>
        {
            if (!CanAccept(token.Pin)) return Task.FromResult(false);
            var operation = active!;
            return checkpointGate.ExecuteAsync(() => CommitFailureCoreAsync(operation, token, outcome));
        }));

    public ValueTask EnumerationEndedAsync(ScanRunContext context, StageOutcome outcome) =>
        new(inbox.ResultOperationAsync(() =>
        {
            if (!CanAccept(context.Pin)
                || active!.Context != context
                || active.Enumeration.State != ScanStageState.Running)
            {
                return Task.FromResult(false);
            }
            var operation = active!;
            return checkpointGate.ExecuteAsync(() => CommitEnumerationAsync(operation, outcome));
        }));

    private bool CanAccept(ScanPin pin) => !disposed
        && active is { Canceled: false, Finalizing: false, Finished: false, Faulted: false }
        && active.Context.Pin == pin
        && current.Status == ScanStatus.Running;

    private async Task<OccurrenceWork?> CommitRegistrationAsync(Operation operation, EnumeratedFile file)
    {
        var mutation = await inbox.CommandAsync(() => operation.Store.PrepareRegistration(file))
            .ConfigureAwait(false);
        return await PersistAndApplyAsync(operation, mutation, fallback: (OccurrenceWork?)null)
            .ConfigureAwait(false);
    }

    private async Task<SignatureCommit> CommitSignatureCoreAsync(
        Operation operation,
        SignatureEvidence evidence)
    {
        var mutation = await inbox.CommandAsync(() => operation.Store.PrepareSignature(evidence))
            .ConfigureAwait(false);
        return await PersistAndApplyAsync(operation, mutation, new SignatureCommit(false, []))
            .ConfigureAwait(false);
    }

    private async Task<bool> CommitComparisonCoreAsync(
        Operation operation,
        ComparisonEvidence evidence)
    {
        var mutation = await inbox.CommandAsync(() => operation.Store.PrepareComparison(evidence))
            .ConfigureAwait(false);
        return await PersistAndApplyAsync(operation, mutation, false).ConfigureAwait(false);
    }

    private async Task<bool> CommitFailureCoreAsync(
        Operation operation,
        OccurrenceToken token,
        StageOutcome outcome)
    {
        var mutation = await inbox.CommandAsync(() => operation.Store.PrepareFailure(token, outcome))
            .ConfigureAwait(false);
        return await PersistAndApplyAsync(operation, mutation, false).ConfigureAwait(false);
    }

    private async Task<T> PersistAndApplyAsync<T>(
        Operation operation,
        ScanStoreMutation<T> mutation,
        T fallback)
    {
        var persisted = await PersistMutationAsync(operation, mutation).ConfigureAwait(false);
        if (!persisted.Succeeded)
        {
            await HandlePersistenceFailureAsync(
                operation, persisted.Failure == ScanCheckpointWriteOutcome.Unavailable).ConfigureAwait(false);
            return fallback;
        }

        return await inbox.CommandAsync(() =>
        {
            foreach (var pair in persisted.ExactRevisions)
                operation.ExactRevisions[pair.Key] = pair.Value;
            var result = operation.Store.Apply(mutation);
            if (!ReferenceEquals(active, operation) || operation.Finished || operation.Faulted)
                return fallback;
            PublishResults(operation);
            return result;
        }).ConfigureAwait(false);
    }

    private async Task<MutationPersistence> PersistMutationAsync<T>(
        Operation operation,
        ScanStoreMutation<T> mutation)
    {
        var revisions = new Dictionary<long, long>(operation.ExactRevisions);
        foreach (var row in mutation.AddedOccurrences)
        {
            var occurrence = new ScanOccurrenceIdentityRecord(
                row.Token, row.DiscoveredPath, row.ResolvedPath, row.PhysicalFileKey);
            var enumeration = new ScanCheckpoint(
                row.Token.Pin.ScanId,
                row.Token.OccurrenceId,
                ScanStage.Enumeration,
                1,
                Guid.NewGuid(),
                row.Enumerate,
                null);
            var exact = new ScanCheckpoint(
                row.Token.Pin.ScanId,
                row.Token.OccurrenceId,
                ScanStage.Exact,
                1,
                Guid.NewGuid(),
                row.Exact,
                new ScanStagePrerequisite(ScanStage.Enumeration, row.Enumerate));
            var result = await checkpoints.WriteInitialOccurrenceAsync(
                new InitialOccurrenceCheckpointCommand(occurrence, enumeration, exact),
                CancellationToken.None).ConfigureAwait(false);
            if (!Succeeded(result)) return new(revisions, result.Outcome);
            revisions[row.Token.OccurrenceId] = 1;
        }

        foreach (var exact in mutation.ExactMutations)
        {
            if (!revisions.TryGetValue(exact.Token.OccurrenceId, out var expectedRevision))
                return new(revisions, ScanCheckpointWriteOutcome.Missing);
            var result = await checkpoints.TransitionStageAsync(
                new ScanStageTransitionCommand(
                    exact.Token.Pin.ScanId,
                    exact.Token.OccurrenceId,
                    ScanStage.Exact,
                    expectedRevision,
                    Guid.NewGuid(),
                    exact.After),
                CancellationToken.None).ConfigureAwait(false);
            if (!Succeeded(result)) return new(revisions, result.Outcome);
            revisions[exact.Token.OccurrenceId] = checked(expectedRevision + 1);
        }
        return new(revisions, null);
    }

    private async Task<bool> CommitEnumerationAsync(Operation operation, StageOutcome outcome)
    {
        var state = await inbox.CommandAsync(() =>
            (operation.LifecycleRevision, operation.LifecycleState)).ConfigureAwait(false);
        var next = outcome.State == ScanStageState.Succeeded
            ? ScanLifecycleState.Exact
            : outcome.Reason == ScanReason.ProgrammingFault
                ? ScanLifecycleState.Faulted
                : ScanLifecycleState.Incomplete;
        var result = await checkpoints.TransitionLifecycleAsync(
            new ScanLifecycleTransitionCommand(
                operation.Context.Pin.ScanId, state.LifecycleRevision, Guid.NewGuid(), next),
            CancellationToken.None).ConfigureAwait(false);
        if (!Succeeded(result) || result.Snapshot?.Lifecycle is not { } lifecycle)
        {
            await HandlePersistenceFailureAsync(
                operation, result.Outcome == ScanCheckpointWriteOutcome.Unavailable).ConfigureAwait(false);
            return false;
        }

        return await inbox.CommandAsync(() =>
        {
            operation.LifecycleRevision = lifecycle.Revision;
            operation.LifecycleState = lifecycle.State;
            if (!ReferenceEquals(active, operation) || operation.Finished || operation.Faulted)
                return false;
            operation.Enumeration = outcome;
            PublishResults(operation);
            return true;
        }).ConfigureAwait(false);
    }

    private async Task RunOperationAsync(Operation operation)
    {
        IScanRun? run = null;
        var faulted = false;
        try
        {
            var inspect = await inbox.CommandAsync(() => ReferenceEquals(active, operation)
                && !operation.Canceled
                && current.Status == ScanStatus.Checking).ConfigureAwait(false);
            if (!inspect) return;
            var evidence = await preflight.InspectAsync(operation.Context.Scope.Selection, operation.Token)
                .ConfigureAwait(false);
            var preparation = await inbox.CommandAsync(() => PrepareStartup(operation, evidence))
                .ConfigureAwait(false);
            if (preparation is null) return;

            var nextLifecycle = preparation.Rejection is null
                ? ScanLifecycleState.Enumerating
                : ScanLifecycleState.Incomplete;
            var durable = await checkpointGate.ExecuteAsync(() =>
                PersistStartupAsync(preparation.Identity, nextLifecycle))
                .ConfigureAwait(false);
            if (!durable.Succeeded)
            {
                await inbox.CommandAsync(() =>
                {
                    ApplyStartupPersistence(operation, durable);
                    return true;
                }).ConfigureAwait(false);
                await HandlePersistenceFailureAsync(operation, durable.Unavailable).ConfigureAwait(false);
                return;
            }
            var start = await inbox.CommandAsync(() =>
            {
                ApplyStartupPersistence(operation, durable);
                if (!ReferenceEquals(active, operation)
                    || operation.Canceled
                    || operation.Faulted
                    || current.Status != ScanStatus.Checking)
                {
                    return false;
                }
                if (preparation.Rejection is { } rejection)
                {
                    operation.Enumeration = rejection;
                    Publish(operation.Store.Snapshot(ScanStatus.Incomplete, rejection));
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
            if (nearDuplicates is not null
                && operation.Context.Options.Near == NearDuplicateMode.Conservative
                && !operation.Token.IsCancellationRequested)
            {
                var rows = await inbox.CommandAsync(() => operation.Store
                    .Snapshot(ScanStatus.Running, operation.Enumeration).Occurrences).ConfigureAwait(false);
                var batch = await nearDuplicates.AnalyzeAsync(
                    operation.Context, rows, operation.Token).ConfigureAwait(false);
                await inbox.CommandAsync(() =>
                {
                    if (!ReferenceEquals(active, operation) || operation.Canceled || operation.Faulted)
                        return false;
                    var mutation = operation.Store.PrepareNearDuplicates(batch);
                    var result = operation.Store.Apply(mutation);
                    if (!result.Accepted) return false;
                    PublishResults(operation);
                    return true;
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { }
        catch (Exception)
        {
            faulted = true;
        }
        finally
        {
            operation.RunReady.TrySetResult(run);
            // Completion and callbacks are separate obligations. Both are observed
            // before resource disposal and before the terminal checkpoint is admitted.
            try
            {
                var callbacks = operation.RequestCancellation();
                var disposal = run is null ? Task.CompletedTask : run.DisposeAsync().AsTask();
                await Task.WhenAll(callbacks, disposal).ConfigureAwait(false);
            }
            catch (Exception)
            {
                faulted = true;
            }
            await operation.DisposeAsync().ConfigureAwait(false);
            var finalize = await inbox.CommandAsync(() =>
            {
                if (operation.Finalizing || operation.Finished) return false;
                operation.Finalizing = true;
                return true;
            }).ConfigureAwait(false);
            if (finalize) await checkpointGate.ExecuteAsync(async () =>
            {
                await FinalizeOperationAsync(operation, faulted).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
    }

    private StartupPreparation? PrepareStartup(Operation operation, ScopePreflightEvidence evidence)
    {
        if (!ReferenceEquals(active, operation)
            || operation.Canceled
            || current.Status != ScanStatus.Checking)
        {
            return null;
        }
        var result = ScopePreflightPolicy.Evaluate(evidence);
        var rejected = result.Decision != ScopePreflightDecision.Ready
            || result.Scope is not { } scope
            || scope.RootIdentity != operation.Context.Scope.RootIdentity
            || scope.Selection != operation.Context.Scope.Selection
            || !SamePath(scope.ResolvedPath, operation.Context.Scope.ResolvedPath);
        var accepted = operation.Context.Scope;
        return new(
            new ScanIdentityRecord(
                operation.Context.Pin,
                accepted.Selection,
                accepted.ResolvedPath,
                accepted.RootIdentity,
                DateTimeOffset.UtcNow),
            rejected
                ? new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety)
                : null);
    }

    private static void ApplyStartupPersistence(
        Operation operation,
        StartupPersistence persistence)
    {
        if (persistence.Lifecycle is { } lifecycle)
        {
            operation.DurableStarted = true;
            operation.LifecycleRevision = lifecycle.Revision;
            operation.LifecycleState = lifecycle.State;
        }
        operation.SuppressLifecycleFinalization |= persistence.SuppressLifecycleFinalization;
    }

    private async Task<StartupPersistence> PersistStartupAsync(
        ScanIdentityRecord identity,
        ScanLifecycleState nextState)
    {
        var identityResult = await identities.PersistScanAsync(identity, CancellationToken.None)
            .ConfigureAwait(false);
        if (identityResult.Outcome is not (
            ScanIdentityPersistOutcome.Persisted or ScanIdentityPersistOutcome.ExactRetry))
        {
            return new(
                null,
                false,
                identityResult.Outcome == ScanIdentityPersistOutcome.Unavailable,
                false);
        }
        var preflight = await checkpoints.TransitionLifecycleAsync(
            new ScanLifecycleTransitionCommand(
                identity.Pin.ScanId, 1, Guid.NewGuid(), ScanLifecycleState.Preflight),
            CancellationToken.None).ConfigureAwait(false);
        if (!Succeeded(preflight) || preflight.Snapshot?.Lifecycle is not { } preflightLifecycle)
        {
            return new(
                null,
                false,
                preflight.Outcome == ScanCheckpointWriteOutcome.Unavailable,
                false);
        }
        var next = await checkpoints.TransitionLifecycleAsync(
            new ScanLifecycleTransitionCommand(
                identity.Pin.ScanId, 1, Guid.NewGuid(), nextState),
            CancellationToken.None).ConfigureAwait(false);
        if (Succeeded(next) && next.Snapshot?.Lifecycle is { } nextLifecycle)
            return new(nextLifecycle, true, false, false);
        if (next.Outcome == ScanCheckpointWriteOutcome.Unavailable)
            return new(preflightLifecycle, false, true, false);

        var canonical = await checkpoints.ReadAsync(identity.Pin.ScanId, CancellationToken.None)
            .ConfigureAwait(false);
        if (canonical.Outcome != ScanCheckpointReadOutcome.Found
            || canonical.Lifecycle is not { } canonicalLifecycle)
        {
            return new(
                preflightLifecycle,
                false,
                canonical.Outcome == ScanCheckpointReadOutcome.Unavailable,
                true);
        }
        if (ScanTransitionPolicy.IsTerminal(canonicalLifecycle.State))
            return new(canonicalLifecycle, false, false, true);

        var faulted = await checkpoints.TransitionLifecycleAsync(
            new ScanLifecycleTransitionCommand(
                identity.Pin.ScanId,
                canonicalLifecycle.Revision,
                Guid.NewGuid(),
                ScanLifecycleState.Faulted),
            CancellationToken.None).ConfigureAwait(false);
        return Succeeded(faulted) && faulted.Snapshot?.Lifecycle is { } faultedLifecycle
            ? new(faultedLifecycle, false, false, true)
            : new(
                canonicalLifecycle,
                false,
                faulted.Outcome == ScanCheckpointWriteOutcome.Unavailable,
                true);
    }

    private async Task FinalizeOperationAsync(Operation operation, bool runFaulted)
    {
        // Entering this gate drains checkpoints admitted before Finalizing closed
        // the owner boundary. Prepare only after that private state is current.
        var preparation = await inbox.CommandAsync(() => PrepareFinalization(operation, runFaulted))
            .ConfigureAwait(false);
        if (preparation is null) return;

        var storeAvailable = !operation.PersistenceUnavailable;
        var stagesDurable = storeAvailable;
        MutationPersistence? persisted = null;
        ScanLifecycleCheckpoint? lifecycle = null;
        if (storeAvailable)
        {
            persisted = await PersistMutationAsync(operation, preparation.Mutation).ConfigureAwait(false);
            stagesDurable = persisted.Succeeded;
            if (persisted.Failure == ScanCheckpointWriteOutcome.Unavailable)
                storeAvailable = false;
        }
        var lifecycleDurable = !operation.DurableStarted
            || ScanTransitionPolicy.IsTerminal(operation.LifecycleState);
        if (storeAvailable
            && operation.DurableStarted
            && !operation.SuppressLifecycleFinalization
            && !ScanTransitionPolicy.IsTerminal(operation.LifecycleState))
        {
            var result = await checkpoints.TransitionLifecycleAsync(
                new ScanLifecycleTransitionCommand(
                    operation.Context.Pin.ScanId,
                    operation.LifecycleRevision,
                    Guid.NewGuid(),
                    preparation.Lifecycle),
                CancellationToken.None).ConfigureAwait(false);
            lifecycleDurable = Succeeded(result) && result.Snapshot?.Lifecycle is not null;
            lifecycle = result.Snapshot?.Lifecycle;
            if (result.Outcome == ScanCheckpointWriteOutcome.Unavailable)
                storeAvailable = false;
        }
        var durable = stagesDurable && lifecycleDurable;
        if (!storeAvailable) operation.PersistenceUnavailable = true;

        await inbox.CommandAsync(() =>
        {
            if (operation.Finished) return true;
            if (durable)
            {
                foreach (var pair in persisted?.ExactRevisions ?? operation.ExactRevisions)
                    operation.ExactRevisions[pair.Key] = pair.Value;
                operation.Store.Apply(preparation.Mutation);
                if (lifecycle is not null)
                {
                    operation.LifecycleRevision = lifecycle.Revision;
                    operation.LifecycleState = lifecycle.State;
                }
                operation.Enumeration = preparation.Enumeration;
            }
            else
            {
                operation.Faulted = true;
                operation.Store.Reconcile(ProgrammingFault);
                operation.Enumeration = ProgrammingFault;
            }
            operation.Finished = true;
            operations.Remove(operation);
            if (ReferenceEquals(active, operation))
            {
                var status = durable ? preparation.Status : ScanStatus.Faulted;
                var enumeration = durable ? preparation.Enumeration : ProgrammingFault;
                Publish(operation.Store.Snapshot(status, enumeration));
            }
            else if (current.HasUnsettledWork != (operations.Count != 0))
            {
                Publish(current);
            }
            operation.Settled.TrySetResult();
            return true;
        }).ConfigureAwait(false);
    }

    private Finalization? PrepareFinalization(Operation operation, bool runFaulted)
    {
        if (operation.Finished) return null;
        var fault = runFaulted
            || operation.Faulted
            || operation.PersistenceUnavailable
            || operation.Store.Faulted
            || current.Status == ScanStatus.Faulted && ReferenceEquals(active, operation);
        var reconcile = fault
            ? ProgrammingFault
            : operation.Canceled
                ? Canceled
                : IncompleteEnumeration;
        var mutation = operation.Store.PrepareReconciliation(reconcile);
        var enumeration = operation.Enumeration;
        if (fault)
        {
            enumeration = ProgrammingFault;
        }
        else if (operation.Canceled)
        {
            enumeration = Canceled;
        }
        else if (enumeration.State is ScanStageState.Pending or ScanStageState.Running)
        {
            enumeration = IncompleteEnumeration;
        }
        var complete = !fault
            && !operation.Canceled
            && !mutation.Next.Incomplete
            && enumeration.State == ScanStageState.Succeeded
            && mutation.Next.PendingComparisons == 0;
        return new(
            mutation,
            fault ? ScanLifecycleState.Faulted
                : complete ? ScanLifecycleState.Complete : ScanLifecycleState.Incomplete,
            fault ? ScanStatus.Faulted : complete ? ScanStatus.Complete : ScanStatus.Incomplete,
            enumeration);
    }

    private async Task HandlePersistenceFailureAsync(Operation operation, bool unavailable)
    {
        await inbox.CommandAsync(() =>
        {
            operation.PersistenceUnavailable |= unavailable;
            operation.Faulted = true;
            if (ReferenceEquals(active, operation))
                Publish(operation.Store.Snapshot(ScanStatus.Faulted, ProgrammingFault));
            return true;
        }).ConfigureAwait(false);
        _ = CancelFaultedOperationAsync(operation);
    }

    private void PublishResults(Operation operation)
    {
        var fault = operation.Faulted
            || operation.Store.Faulted
            || operation.Enumeration.Reason == ScanReason.ProgrammingFault;
        Publish(operation.Store.Snapshot(
            fault ? ScanStatus.Faulted : ScanStatus.Running,
            fault ? ProgrammingFault : operation.Enumeration));
    }

    private void Publish(ScanSnapshot snapshot)
    {
        snapshot = snapshot with { HasUnsettledWork = operations.Count != 0 };
        var terminalIsDurable = active is { DurableStarted: true } terminalOperation
            && snapshot.Pin == terminalOperation.Context.Pin
            && ScanTransitionPolicy.IsTerminal(terminalOperation.LifecycleState);
        Operation? newlyFaulted = null;
        if (snapshot.Status == ScanStatus.Faulted
            && active is { } operation
            && snapshot.Pin == operation.Context.Pin)
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
            try
            {
                ((EventHandler<ScanSnapshot>)observer)(this, snapshot);
            }
            catch (Exception)
            {
                failed = true;
            }
        }
        if (!failed || snapshot.Status == ScanStatus.Faulted || terminalIsDurable) return;
        // A throwing observer cannot prevent acknowledgment or hide the fault.
        Publish(snapshot with { Status = ScanStatus.Faulted, Enumeration = ProgrammingFault });
    }

    private async Task CancelFaultedOperationAsync(Operation operation)
    {
        // Crossing another non-reentrant turn ensures that fault publication is
        // finished before cancellation effects run. Admitted results keep draining.
        var cancel = await inbox.CommandAsync(() => ReferenceEquals(active, operation)
            && operation.Faulted).ConfigureAwait(false);
        if (!cancel) return;
        try
        {
            await operation.RequestCancellation().ConfigureAwait(false);
        }
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
        await checkpointGate.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static bool Succeeded(ScanCheckpointWriteResult result) =>
        result.Outcome is ScanCheckpointWriteOutcome.Accepted or ScanCheckpointWriteOutcome.Idempotent;

    private static bool SamePath(string left, string right) =>
        ScopePathContainment.Contains(left, right) && ScopePathContainment.Contains(right, left);

    private sealed record StartupPreparation(
        ScanIdentityRecord Identity,
        StageOutcome? Rejection);

    private sealed record StartupPersistence(
        ScanLifecycleCheckpoint? Lifecycle,
        bool Succeeded,
        bool Unavailable,
        bool SuppressLifecycleFinalization);

    private sealed record MutationPersistence(
        Dictionary<long, long> ExactRevisions,
        ScanCheckpointWriteOutcome? Failure)
    {
        public bool Succeeded => Failure is null;
    }

    private sealed record Finalization(
        ScanStoreMutation<bool> Mutation,
        ScanLifecycleState Lifecycle,
        ScanStatus Status,
        StageOutcome Enumeration);

    // Lifecycle synchronization protects cancellation-resource ownership only.
    // Accepted state is exclusively mutated by ScanOwnerInbox turns.
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
        public Dictionary<long, long> ExactRevisions { get; } = [];
        public CancellationTokenRegistration CallerCancellation { get; set; }
        public TaskCompletionSource<IScanRun?> RunReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StageOutcome Enumeration { get; set; } = Pending;
        public ScanLifecycleState LifecycleState { get; set; } = ScanLifecycleState.Preflight;
        public long LifecycleRevision { get; set; } = 1;
        public bool DurableStarted { get; set; }
        public bool SuppressLifecycleFinalization { get; set; }
        public bool PersistenceUnavailable { get; set; }
        public bool Canceled { get; set; }
        public bool Finalizing { get; set; }
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
            lock (lifecycle)
            {
                resourcesDisposed = true;
                source.Dispose();
            }
        }
    }

    private sealed class CheckpointGate : IAsyncDisposable
    {
        private readonly Channel<IWorkItem> queue = Channel.CreateBounded<IWorkItem>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        private readonly Task processor;

        public CheckpointGate() => processor = Task.Run(ProcessAsync);

        public Task<T> ExecuteAsync<T>(Func<Task<T>> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            var item = new WorkItem<T>(work);
            _ = EnqueueAsync(item);
            return item.Completion.Task;
        }

        private async Task EnqueueAsync(IWorkItem item)
        {
            try
            {
                await queue.Writer.WriteAsync(item).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                item.Fail(exception);
            }
        }

        private async Task ProcessAsync()
        {
            await foreach (var item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                await item.RunAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            queue.Writer.TryComplete();
            await processor.ConfigureAwait(false);
        }

        private interface IWorkItem
        {
            Task RunAsync();
            void Fail(Exception exception);
        }

        private sealed class WorkItem<T>(Func<Task<T>> work) : IWorkItem
        {
            public TaskCompletionSource<T> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task RunAsync()
            {
                try
                {
                    Completion.TrySetResult(await work().ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    Completion.TrySetException(exception);
                }
            }

            public void Fail(Exception exception) => Completion.TrySetException(exception);
        }
    }
}
