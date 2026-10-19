using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Albumen.Application.Review;
using Albumen.Application.Shell;
using Albumen.Domain.Action;
using Albumen.Domain.Review;

namespace Albumen.Application.Action;

public sealed class ActionCoordinator : IActionCoordinator
{
    private readonly IActionExecutionBoundaryObserver? boundaryObserver;
    private readonly ActionOwnerInbox inbox;
    private readonly IApprovedActionAuthority authority;
    private readonly IOverrideEvidenceStore evidenceStore;
    private readonly IActionJournalStore journal;
    private readonly IRecycleBinExecutor executor;
    private readonly Dictionary<Guid, Operation> operations = [];
    private readonly Queue<(Operation Operation, ActionSnapshot Snapshot)> publications = [];
    private readonly Queue<Operation> cancellations = [];
    private ActionSnapshot current = ActionSnapshot.Idle;
    private Operation? active;
    private bool disposed;
    private bool unhealthy;

    public ActionCoordinator(IWorkflowScheduler scheduler, IApprovedActionAuthority authority,
        IOverrideEvidenceStore evidenceStore, IActionJournalStore journal, IRecycleBinExecutor executor,
        int resultCapacity = 16)
    {
        inbox = new(scheduler, resultCapacity) { DeliverAfterTurn = DeliverPublications };
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }
    internal ActionCoordinator(IWorkflowScheduler scheduler, IApprovedActionAuthority authority,
        IOverrideEvidenceStore evidenceStore, IActionJournalStore journal, IRecycleBinExecutor executor,
        IActionExecutionBoundaryObserver boundaryObserver, int resultCapacity = 16)
        : this(scheduler, authority, evidenceStore, journal, executor, resultCapacity) =>
        this.boundaryObserver = boundaryObserver ?? throw new ArgumentNullException(nameof(boundaryObserver));
    public ActionSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<ActionSnapshot>? SnapshotChanged;

    public async Task<ActionCommandResult> ApproveAsync(ApprovedActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The complete immutable value is encoded before admission. No content
        // I/O, authority work or adapter execution occurs on the owner lane.
        var valid = ValidRequest(request);
        var hash = valid ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request))) : null;
        var start = await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!valid) return new Start(new(ActionCommandOutcome.Rejected, current), null);
            if (operations.TryGetValue(request.RequestId, out var existing))
                return new Start(new(existing.PayloadHash == hash && ReviewStore.SameActionRequest(existing.Request, request)
                    ? ActionCommandOutcome.Existing : ActionCommandOutcome.Rejected, existing.Store.Current), null);
            if (disposed || unhealthy || active is { Finished: false })
                return new Start(new(ActionCommandOutcome.Rejected, current), null);
            var operation = new Operation(request, hash!, cancellationToken);
            operations.Add(request.RequestId, operation);
            active = operation;
            Publish(operation);
            return new Start(null, operation);
        }).ConfigureAwait(false);
        if (start.Operation is not { } admitted) return start.Result!;
        _ = Task.Run(() => RunAsync(admitted), CancellationToken.None);
        return await admitted.Completion.Task.ConfigureAwait(false);
    }

    public async Task ReportObserverFaultAsync() => await inbox.CommandAsync(() =>
    {
        if (active is { } operation && operation.Store.Current.Status != ActionStatus.Faulted)
            Fault(operation, ActionSafetyReason.ProgrammingFault);
        return true;
    }).ConfigureAwait(false);

    public async Task ReportObserverFaultAsync(Guid requestId) => await inbox.CommandAsync(() =>
    {
        // A delayed projection failure belongs only to its originating request.
        // It cannot stop a subsequently admitted operation or republish old work.
        if (active is { } operation && operation.Request.RequestId == requestId
            && operation.Store.Current.Status != ActionStatus.Faulted)
            Fault(operation, ActionSafetyReason.ProgrammingFault);
        return true;
    }).ConfigureAwait(false);

    private async Task RunAsync(Operation operation)
    {
        var registration = operation.Caller.Register(() => _ = operation.RequestCancellation());
        try
        {
            if (!await StoppedAsync(operation).ConfigureAwait(false))
                await ClaimAndRunAsync(operation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested || operation.Caller.IsCancellationRequested)
        {
            // Admitted work still settles and any durable predecessor remains
            // inspectable. Executor exceptions are handled at its own boundary.
        }
        catch (Exception)
        {
            await FaultAsync(operation, ActionSafetyReason.ProgrammingFault).ConfigureAwait(false);
        }
        try
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await operation.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FaultAsync(operation, ActionSafetyReason.ProgrammingFault).ConfigureAwait(false);
        }
        await inbox.CommandAsync(() =>
        {
            operation.Store.Finish(operation.Store.Current.FaultReason is ActionSafetyReason.None
                ? ActionSafetyReason.Interrupted : operation.Store.Current.FaultReason);
            Publish(operation);
            return true;
        }).ConfigureAwait(false);
        // The publish-turn acknowledgment follows synchronous observer delivery.
        // Any observer-fault command is already queued before this FIFO barrier;
        // its Faulted publication must drain before the final snapshot is read.
        var result = await inbox.CommandAsync(() =>
        {
            operation.Finished = true;
            var snapshot = operation.Store.Current;
            return new ActionCommandResult(snapshot.Status switch
            {
                ActionStatus.Complete => ActionCommandOutcome.Completed,
                ActionStatus.Faulted => ActionCommandOutcome.Faulted,
                _ => ActionCommandOutcome.Incomplete,
            }, snapshot);
        }).ConfigureAwait(false);
        // Completion is also the disposal drain. Resolve it only outside the
        // acknowledged final turn, when no final observer/fault work remains.
        operation.Completion.TrySetResult(result);
    }

    private async Task ClaimAndRunAsync(Operation operation)
    {
        var reads = ImmutableArray.CreateBuilder<OverrideReadResult>();
        foreach (var id in operation.Request.Items.Where(item => item.OverrideEvidenceId is not null)
                     .Select(item => item.OverrideEvidenceId!.Value).Distinct().OrderBy(id => id.Value))
        {
            OverrideReadResult read;
            try { read = await evidenceStore.ReadAsync(id, operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested) { throw; }
            catch (Exception) { read = new(OverrideReadOutcome.Unavailable, null); }
            if (read is not { Outcome: OverrideReadOutcome.Found, Evidence: { } evidence } || evidence.EvidenceId != id)
            {
                await RejectWaitingAsync(operation, read.Outcome == OverrideReadOutcome.Missing
                    ? ActionSafetyReason.OverrideMissing : ActionSafetyReason.OverrideMismatch).ConfigureAwait(false);
                return;
            }
            reads.Add(read);
        }
        if (await StoppedAsync(operation).ConfigureAwait(false)) return;
        var claimResult = await authority.ClaimAsync(operation.Request, reads.ToImmutable(), operation.Token).ConfigureAwait(false);
        if (await StoppedAsync(operation).ConfigureAwait(false)) return;
        if (claimResult is not { Outcome: ApprovedActionClaimOutcome.Claimed, Claim: { } claim }
            || claim.ClaimId.Value == Guid.Empty || !ReviewStore.SameActionRequest(operation.Request, claim.Request))
        {
            await RejectWaitingAsync(operation, claimResult.Reason == ActionSafetyReason.None
                ? ActionSafetyReason.StalePin : claimResult.Reason).ConfigureAwait(false);
            if (claimResult.Outcome == ApprovedActionClaimOutcome.Faulted)
                await FaultAsync(operation, claimResult.Reason).ConfigureAwait(false);
            return;
        }
        await UpdateAsync(operation, store => store.Running()).ConfigureAwait(false);
        for (var index = 0; index < operation.Request.Items.Length; index++)
        {
            if (await StoppedAsync(operation).ConfigureAwait(false)) return;
            var item = operation.Request.Items[index];
            var preceding = await inbox.ResultAsync(() => operation.Store.Preceding(index)).ConfigureAwait(false);
            if (preceding.Any(outcome => outcome.GroupId == item.GroupId
                && outcome.Disposition is PriorActionDisposition.Unknown or PriorActionDisposition.Pending))
            {
                await UpdateAsync(operation, store => store.Reject(index, ActionSafetyReason.DependentOutcomeUnknown)).ConfigureAwait(false);
                continue;
            }
            var refreshed = await authority.RefreshSafetyAsync(claim, item, preceding, operation.Token).ConfigureAwait(false);
            if (await StoppedAsync(operation).ConfigureAwait(false)) return;
            if (refreshed is not { Outcome: ApprovedActionClaimOutcome.Claimed, Envelope: { } safety }
                || !ValidEnvelope(safety, claim, item, preceding))
            {
                await UpdateAsync(operation, store => store.Reject(index, refreshed.Reason == ActionSafetyReason.None
                    ? ActionSafetyReason.StalePin : refreshed.Reason)).ConfigureAwait(false);
                if (refreshed.Outcome == ApprovedActionClaimOutcome.Faulted)
                {
                    await FaultAsync(operation, refreshed.Reason).ConfigureAwait(false);
                    return;
                }
                continue;
            }
            var decision = ActionSafetyPolicy.Evaluate(safety.Input);
            if (decision.Kind != ActionSafetyDecisionKind.Allowed)
            {
                await UpdateAsync(operation, store => store.Reject(index, decision.Reason)).ConfigureAwait(false);
                continue;
            }
            var evidence = item.OverrideEvidenceId is { } evidenceId
                ? reads.Single(read => read.Evidence!.EvidenceId == evidenceId).Evidence : null;
            if (!await RunItemAsync(operation, index, safety, evidence).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> RunItemAsync(Operation operation, int index, ActionSafetyEnvelope safety,
        DeliberateOverrideEvidence? evidence)
    {
        if (await StoppedAsync(operation).ConfigureAwait(false)) return false;
        boundaryObserver?.Observe(ActionExecutionBoundary.BeforeRequestedCommit, index);
        var id = Guid.NewGuid();
        var requested = new ActionRequestedFact(id, DateTimeOffset.UtcNow, safety, operation.PayloadHash, evidence);
        // Once a write is admitted, its acknowledgment is never canceled. The
        // next owner turn records exactly what the journal acknowledged.
        var written = await WriteAsync(() => journal.PersistRequestedAsync(requested, CancellationToken.None)).ConfigureAwait(false);
        await UpdateAsync(operation, store =>
        {
            if (written.Outcome is ActionJournalWriteOutcome.Persisted or ActionJournalWriteOutcome.ExactRetry)
                store.Requested(index, id);
        }).ConfigureAwait(false);
        if (written.Outcome != ActionJournalWriteOutcome.Persisted)
        {
            await FaultAsync(operation, JournalReason(written)).ConfigureAwait(false);
            return false;
        }
        boundaryObserver?.Observe(ActionExecutionBoundary.AfterRequestedCommitBeforeExecuting, index);
        if (await StoppedAsync(operation).ConfigureAwait(false)) return false;
        var executing = new ActionExecutingFact(id, DateTimeOffset.UtcNow);
        written = await WriteAsync(() => journal.PersistExecutingAsync(executing, CancellationToken.None)).ConfigureAwait(false);
        await UpdateAsync(operation, store =>
        {
            if (written.Outcome is ActionJournalWriteOutcome.Persisted or ActionJournalWriteOutcome.ExactRetry)
                store.Executing(index);
        }).ConfigureAwait(false);
        if (written.Outcome != ActionJournalWriteOutcome.Persisted)
        {
            await FaultAsync(operation, JournalReason(written)).ConfigureAwait(false);
            return false;
        }
        // Without an executor acknowledgment there is no terminal evidence.
        // Retain the exact Executing predecessor for reconciliation; Finish
        // projects its uncertainty without inventing a terminal journal row.
        if (await StoppedAsync(operation).ConfigureAwait(false)) return false;
        boundaryObserver?.Observe(ActionExecutionBoundary.AfterExecutingCommitBeforeAdapter, index);
        RecycleBinResult result;
        try { result = await executor.ExecuteAsync(new(id, safety), operation.Token).ConfigureAwait(false); }
        catch (Exception)
        {
            // A thrown/canceled adapter acknowledgment cannot prove that
            // permission was withheld. Persist uncertainty without replay.
            result = new(RecycleBinOutcomeKind.UnknownPendingRevalidation, ActionSafetyReason.ProgrammingFault,
                new(0, 0, false, null, null, 0, 0, null, false, true, false));
        }
        boundaryObserver?.Observe(ActionExecutionBoundary.AfterAdapterReturnBeforeTerminalCommit, index);
        var terminal = new ActionTerminalFact(id, DateTimeOffset.UtcNow, result.Outcome switch
        {
            RecycleBinOutcomeKind.Recycled => ActionTerminalKind.Recycled,
            RecycleBinOutcomeKind.NotRemoved => ActionTerminalKind.NotRemoved,
            _ => ActionTerminalKind.UnknownPendingRevalidation,
        }, result.Reason, result.Evidence);
        written = await WriteAsync(() => journal.PersistTerminalAsync(terminal, CancellationToken.None)).ConfigureAwait(false);
        if (written.Outcome is not (ActionJournalWriteOutcome.Persisted or ActionJournalWriteOutcome.ExactRetry))
        {
            await UpdateAsync(operation, store => store.Uncertain(index, JournalReason(written))).ConfigureAwait(false);
            await FaultAsync(operation, JournalReason(written)).ConfigureAwait(false);
            return false;
        }
        boundaryObserver?.Observe(ActionExecutionBoundary.AfterTerminalCommitBeforePublication, index);
        await UpdateAsync(operation, store => store.Terminal(index, terminal.Kind, terminal.Reason)).ConfigureAwait(false);
        if (written.Outcome == ActionJournalWriteOutcome.ExactRetry)
        {
            // An exact receipt confirms the immutable terminal truth, but an
            // unexpected existing fact never grants permission for later work.
            await FaultAsync(operation, JournalReason(written)).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private static async Task<ActionJournalWriteResult> WriteAsync(Func<ValueTask<ActionJournalWriteResult>> write)
    {
        try { return await write().ConfigureAwait(false); }
        catch (Exception) { return new(ActionJournalWriteOutcome.Unavailable); }
    }
    private static ActionSafetyReason JournalReason(ActionJournalWriteResult result) =>
        result.Outcome == ActionJournalWriteOutcome.Unavailable ? ActionSafetyReason.JournalUnavailable : ActionSafetyReason.JournalConflict;
    private Task<bool> StoppedAsync(Operation operation) => inbox.ResultAsync(() =>
        operation.StopRequested || operation.Caller.IsCancellationRequested || operation.Token.IsCancellationRequested);
    private Task<bool> UpdateAsync(Operation operation, System.Action<ActionStore> change) => inbox.ResultAsync(() =>
    {
        if (operation.Finished) return false;
        change(operation.Store);
        Publish(operation);
        return true;
    });
    private Task<bool> RejectWaitingAsync(Operation operation, ActionSafetyReason reason) => UpdateAsync(operation, store =>
    {
        for (var index = 0; index < store.Current.Items.Length; index++)
            if (store.Current.Items[index].State == ActionItemState.Waiting) store.Reject(index, reason);
    });
    private Task<bool> FaultAsync(Operation operation, ActionSafetyReason reason) => inbox.CommandAsync(() =>
    {
        Fault(operation, reason);
        return true;
    });
    private void Fault(Operation operation, ActionSafetyReason reason)
    {
        unhealthy = true;
        operation.StopRequested = true;
        operation.Store.Fault(reason);
        cancellations.Enqueue(operation);
        Publish(operation);
    }
    private void Publish(Operation operation)
    {
        var snapshot = operation.Store.Current;
        if (!ReferenceEquals(active, operation) || ReferenceEquals(snapshot, current)) return;
        Volatile.Write(ref current, snapshot);
        publications.Enqueue((operation, snapshot));
    }
    private void DeliverPublications()
    {
        while (publications.TryDequeue(out var publication))
        {
            var failed = false;
            foreach (var observer in SnapshotChanged?.GetInvocationList() ?? [])
            {
                try { ((EventHandler<ActionSnapshot>)observer)(this, publication.Snapshot); }
                catch (Exception) { failed = true; }
            }
            if (failed && publication.Snapshot.Status != ActionStatus.Faulted)
                _ = inbox.CommandAsync(() =>
                {
                    if (publication.Operation.Store.Current.Status != ActionStatus.Faulted)
                        Fault(publication.Operation, ActionSafetyReason.ProgrammingFault);
                    return true;
                });
        }
        while (cancellations.TryDequeue(out var operation)) _ = operation.RequestCancellation();
    }
    public async ValueTask DisposeAsync()
    {
        var pending = await inbox.CommandAsync(() =>
        {
            disposed = true;
            foreach (var operation in operations.Values.Where(value => !value.Finished))
            {
                operation.StopRequested = true;
                cancellations.Enqueue(operation);
            }
            return operations.Values.Select(operation => operation.Completion.Task).ToArray();
        }).ConfigureAwait(false);
        await Task.WhenAll(pending).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
    private static bool ValidRequest(ApprovedActionRequest request)
    {
        if (request.RequestId == Guid.Empty || request.ReviewSessionId == Guid.Empty || request.SelectionRevision <= 0
            || request.Scan is null || request.Scan.ScanId == Guid.Empty || request.Scan.ScopeRevision <= 0
            || request.Scan.RuleSetRevision <= 0 || request.Scan.AnalysisRevision <= 0
            || request.Scope is null || request.Scope.Selection is null || string.IsNullOrWhiteSpace(request.Scope.ResolvedPath)
            || request.Disclosure is null || request.Items.IsDefaultOrEmpty || request.SelectedOccurrenceIds.IsDefault
            || request.Items.Length != request.SelectedOccurrenceIds.Length) return false;
        long previous = 0;
        foreach (var item in request.Items)
        {
            if (item is null || item.GroupId == Guid.Empty || item.GroupRevision <= 0 || item.Occurrence is null
                || item.Occurrence.Pin != request.Scan || item.Occurrence.OccurrenceId <= previous
                || item.Occurrence.OccurrenceRevision <= 0 || item.Occurrence.Attempt <= 0
                || string.IsNullOrWhiteSpace(item.DiscoveredPath) || string.IsNullOrWhiteSpace(item.ResolvedPath)
                || item.Content is null || item.Content.PhysicalFileKey != item.PhysicalFileKey || item.Allocation is null
                || item.OverrideEvidenceId is { Value: var evidenceId } && evidenceId == Guid.Empty) return false;
            previous = item.Occurrence.OccurrenceId;
        }
        return request.Items.Select(item => item.Occurrence.OccurrenceId).SequenceEqual(request.SelectedOccurrenceIds);
    }
    private static bool ValidEnvelope(ActionSafetyEnvelope envelope, ApprovedActionClaim claim,
        ApprovedActionItem item, ImmutableArray<PriorActionOutcome> preceding) =>
        envelope.Claim is not null && envelope.Claim.ClaimId == claim.ClaimId
        && ReviewStore.SameActionRequest(envelope.Claim.Request, claim.Request) && envelope.Item == item
        && !envelope.PrecedingOutcomes.IsDefault && envelope.PrecedingOutcomes.SequenceEqual(preceding)
        && envelope.Input is not null && envelope.Input.ApprovedGroup.GroupId == item.GroupId
        && envelope.Input.ApprovedGroup.GroupRevision == item.GroupRevision && envelope.Input.ApprovedGroup.Pin == claim.Request.Scan
        && envelope.Decision.Kind == ActionSafetyDecisionKind.Allowed;

    private sealed record Start(ActionCommandResult? Result, Operation? Operation);

    // This lock owns only cancellation-task/source lifetime. All accepted Action
    // state, stop flags, reservations and snapshots are owned by inbox turns.
    private sealed class Operation : IAsyncDisposable
    {
        private readonly object lifecycle = new();
        private readonly CancellationTokenSource cancellation = new();
        private Task? cancellationTask;
        private bool closed;
        internal Operation(ApprovedActionRequest request, string payloadHash, CancellationToken caller)
        {
            Request = request;
            PayloadHash = payloadHash;
            Caller = caller;
            Token = cancellation.Token;
            Store = new(request);
        }
        internal ApprovedActionRequest Request { get; }
        internal string PayloadHash { get; }
        internal CancellationToken Caller { get; }
        internal CancellationToken Token { get; }
        internal ActionStore Store { get; }
        internal bool StopRequested { get; set; }
        internal bool Finished { get; set; }
        internal TaskCompletionSource<ActionCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task RequestCancellation()
        {
            lock (lifecycle)
                return cancellationTask ??= closed ? Task.CompletedTask
                    : Task.Run(() => cancellation.CancelAsync(), CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {
            Task pending;
            lock (lifecycle)
            {
                closed = true;
                pending = cancellationTask ?? Task.CompletedTask;
            }
            try { await pending.ConfigureAwait(false); }
            finally { cancellation.Dispose(); }
            GC.SuppressFinalize(this);
        }
    }
}

internal enum ActionExecutionBoundary
{
    BeforeRequestedCommit,
    AfterRequestedCommitBeforeExecuting,
    AfterExecutingCommitBeforeAdapter,
    AfterAdapterReturnBeforeTerminalCommit,
    AfterTerminalCommitBeforePublication,
}

internal interface IActionExecutionBoundaryObserver
{
    void Observe(ActionExecutionBoundary boundary, int itemIndex);
}
