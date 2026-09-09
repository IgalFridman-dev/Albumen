using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

public sealed class ReviewCoordinator : IReviewCoordinator, IReviewResultSink
{
    private readonly ReviewOwnerInbox inbox;
    private readonly IReviewRevalidationExecutor revalidation;
    private readonly Queue<(ReviewSnapshot Snapshot, Operation? Operation)> publications = new();
    private readonly HashSet<Operation> operations = [];
    private readonly HashSet<ScanPin> revoked = [];
    private readonly Dictionary<Guid, long> groupRevisions = [];
    private readonly HashSet<Guid> retiredGroups = [];
    private ScanPin? requiredScan;
    private ScanPin? watermark;
    private bool admissionRestricted;
    private bool disposed;
    private Operation? active;
    private ReviewSnapshot current = ReviewSnapshot.Closed;
    public ReviewCoordinator(IWorkflowScheduler scheduler, IReviewRevalidationExecutor revalidation,
        IOverrideEvidenceStore evidenceStore, int resultCapacity = 16)
    {
        inbox = new(scheduler, resultCapacity);
        inbox.DeliverAfterTurn = DeliverPublications;
        this.revalidation = revalidation ?? throw new ArgumentNullException(nameof(revalidation));
        ArgumentNullException.ThrowIfNull(evidenceStore);
    }
    public ReviewSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<ReviewSnapshot>? SnapshotChanged;

    public async Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transition = await inbox.CommandAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidOpen(request)) return (Result: new ReviewCommandResult(ReviewCommandOutcome.Invalid, current), Next: (Operation?)null, Old: (Operation?)null);
            var pin = request.Scan.Pin!;
            if (IsOlder(pin) || revoked.Contains(pin) || (admissionRestricted && pin != requiredScan)
                || request.Scan.Groups.Any(g => retiredGroups.Contains(g.GroupId)
                    || (groupRevisions.TryGetValue(g.GroupId, out var revision) && g.GroupRevision < revision)))
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Stale, current), Next: (Operation?)null, Old: (Operation?)null);
            var old = active;
            if (old is not null)
            {
                old.Invalidated = true;
                if (old.Request.Scan.Pin != pin) revoked.Add(old.Request.Scan.Pin!);
                foreach (var group in old.Request.Scan.Groups)
                    if (!request.Scan.Groups.Any(g => g.GroupId == group.GroupId)) retiredGroups.Add(group.GroupId);
            }
            foreach (var group in request.Scan.Groups) groupRevisions[group.GroupId] = group.GroupRevision;
            watermark = pin;
            var next = new Operation(new(Guid.NewGuid(), pin), request);
            active = next;
            operations.Add(next);
            Publish(next.Store.Current);
            return (Result: new ReviewCommandResult(ReviewCommandOutcome.Committed, current), Next: (Operation?)next, Old: old);
        }).ConfigureAwait(false);
        if (transition.Old is { } previous) _ = previous.RequestCancellation();
        if (transition.Next is { } operation)
            _ = RunOperationAsync(operation, cancellationToken);
        return transition.Result;
    }

    public Task<ReviewCommandResult> ChangeSelectionAsync(SelectionCommand command, CancellationToken cancellationToken) =>
        inbox.CommandAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || active is null || active.Invalidated) return new ReviewCommandResult(ReviewCommandOutcome.Stale, current);
            try
            {
                var result = active.Store.ChangeSelection(command);
                if (!ReferenceEquals(result.Snapshot, current)) Publish(result.Snapshot);
                return result;
            }
            catch (Exception)
            {
                Fault(active);
                return new ReviewCommandResult(ReviewCommandOutcome.Faulted, current);
            }
        });

    public async Task InvalidateAsync(ScanPin? currentScan)
    {
        var old = await inbox.CommandAsync(() =>
        {
            if (currentScan is not null && (IsOlder(currentScan) || revoked.Contains(currentScan))) return null;
            if (active?.Request.Scan.Pin == currentScan && currentScan is not null) return null;
            admissionRestricted = true;
            if (requiredScan is not null && requiredScan != currentScan) revoked.Add(requiredScan);
            requiredScan = currentScan;
            if (currentScan is not null) watermark = currentScan;
            var previous = active;
            if (previous is not null)
            {
                previous.Invalidated = true;
                revoked.Add(previous.Request.Scan.Pin!);
                active = null;
                Publish(ReviewSnapshot.Closed);
            }
            return previous;
        }).ConfigureAwait(false);
        if (old is not null) _ = old.RequestCancellation();
    }

    public async ValueTask<bool> CommitRevalidationAsync(ReviewRevalidationEvidence evidence) =>
        await inbox.ResultAsync(() => AcceptResult(evidence.Request.Session, store => store.Accept(evidence))).ConfigureAwait(false);
    public async ValueTask<bool> FailRevalidationAsync(ReviewRevalidationRequest request, ReviewUnavailableReason reason) =>
        await inbox.ResultAsync(() => AcceptResult(request.Session, store => store.Fail(request, reason))).ConfigureAwait(false);

    private bool AcceptResult(ReviewSessionPin session, Func<ReviewStore, bool> accept)
    {
        if (disposed || active is not { Invalidated: false, Finished: false } operation || operation.Session != session
            || current.Status == ReviewStatus.Faulted) return false;
        try
        {
            var accepted = accept(operation.Store);
            if (accepted) Publish(operation.Store.Current);
            return accepted;
        }
        catch (Exception) { Fault(operation); return false; }
    }

    private bool IsOlder(ScanPin pin) => watermark is { } latest
        && (pin.ScopeRevision < latest.ScopeRevision || pin.RuleSetRevision < latest.RuleSetRevision
            || (pin.ScanId == latest.ScanId && pin.AnalysisRevision < latest.AnalysisRevision));

    private static bool ValidOpen(ReviewOpenRequest request)
    {
        if (request.Scan.Status is not (ScanStatus.Complete or ScanStatus.Incomplete)
            || request.Scan.Pin is not { } pin || pin.ScanId == Guid.Empty || pin.AnalysisRevision <= 0
            || request.Scope.ScopeState != ScopeSessionState.Ready
            || request.Scope.Preflight is not { Decision: ScopePreflightDecision.Ready, Scope: { } scope }
            || request.Scope.Selection != scope.Selection || request.Scope.Revision != pin.ScopeRevision
            || pin.ScopeRevision <= 0 || request.Rules.ScopeRevision != pin.ScopeRevision
            || request.Rules.RuleSetRevision != pin.RuleSetRevision || pin.RuleSetRevision <= 0
            || request.Scan.Groups.IsDefault || request.Scan.Occurrences.IsDefault) return false;
        var ids = new HashSet<long>();
        var groups = new HashSet<Guid>();
        foreach (var group in request.Scan.Groups)
        {
            if (group.Pin != pin || group.Category != CandidateCategory.Exact || group.GroupId == Guid.Empty
                || !groups.Add(group.GroupId) || group.Members.IsDefault) return false;
            foreach (var member in group.Members)
            {
                if (member.Token.Pin != pin || member.Token.OccurrenceRevision <= 0 || member.Token.Attempt <= 0
                    || !ids.Add(member.Token.OccurrenceId) || member.Content is null || member.PhysicalFileKey is null
                    || member.Exact.State != ScanStageState.Succeeded || !request.Scan.Occurrences.Contains(member)) return false;
            }
        }
        return true;
    }

    private async Task RunOperationAsync(Operation operation, CancellationToken caller)
    {
        IReviewRun? run = null;
        var stopCaller = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = caller.Register(() => canceled.TrySetResult());
        var callerWatcher = ObserveCallerAsync(operation, canceled.Task, stopCaller.Task);
        var faulted = false;
        try
        {
            var start = await inbox.CommandAsync(() => !disposed && ReferenceEquals(active, operation)
                && !operation.Invalidated && current.Status != ReviewStatus.Faulted).ConfigureAwait(false);
            if (start)
            {
                // Executor Start may perform synchronous work; it never runs on
                // a scheduler/owner callback or on the UI caller's continuation.
                run = await Task.Run(() => revalidation.Start(operation.Session,
                    new(operation.Session.Scan, operation.Request.Scope.Preflight!.Scope!), operation.Request.Scan.Groups, this),
                    CancellationToken.None).ConfigureAwait(false);
                operation.RunReady.TrySetResult(run);
                await run.Completion.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            faulted = true;
            await inbox.CommandAsync(() =>
            {
                if (ReferenceEquals(active, operation)) Fault(operation);
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            operation.RunReady.TrySetResult(run);
            stopCaller.TrySetResult();
            try
            {
                await callerWatcher.ConfigureAwait(false);
                // Cancellation callbacks can await run Completion and submit
                // stale results. Both obligations finish before disposal begins.
                await operation.RequestCancellation().ConfigureAwait(false);
            }
            catch (Exception) { faulted = true; }
            try { if (run is not null) await run.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { faulted = true; }
            await registration.DisposeAsync().ConfigureAwait(false);
            await inbox.CommandAsync(() =>
            {
                operation.Finished = true;
                if (ReferenceEquals(active, operation))
                {
                    if (faulted || operation.Store.Current.Status == ReviewStatus.Faulted) operation.Store.Fault();
                    else operation.Store.Finish(ReviewUnavailableReason.ReadFailure);
                    Publish(operation.Store.Current);
                }
                operations.Remove(operation);
                operation.Settled.TrySetResult();
                return true;
            }).ConfigureAwait(false);
        }
    }

    private async Task ObserveCallerAsync(Operation operation, Task canceled, Task stopped)
    {
        if (await Task.WhenAny(canceled, stopped).ConfigureAwait(false) != canceled) return;
        await inbox.CommandAsync(() =>
        {
            operation.Invalidated = true;
            if (ReferenceEquals(active, operation))
            {
                operation.Store.Finish(ReviewUnavailableReason.Canceled);
                Publish(operation.Store.Current);
            }
            return true;
        }).ConfigureAwait(false);
        await operation.RequestCancellation().ConfigureAwait(false);
    }

    private void Publish(ReviewSnapshot snapshot)
    {
        Volatile.Write(ref current, snapshot);
        publications.Enqueue((snapshot, active));
    }

    private void DeliverPublications()
    {
        while (publications.TryDequeue(out var publication))
        {
            var failed = false;
            foreach (var observer in SnapshotChanged?.GetInvocationList() ?? [])
            {
                try { ((EventHandler<ReviewSnapshot>)observer)(this, publication.Snapshot); }
                catch (Exception) { failed = true; }
            }
            if (publication.Operation is { } operation)
            {
                if (publication.Snapshot.Status == ReviewStatus.Faulted) _ = operation.RequestCancellation();
                else if (failed) _ = ObserveObserverFaultAsync(operation);
            }
        }
    }

    private async Task ObserveObserverFaultAsync(Operation operation)
    {
        await inbox.CommandAsync(() =>
        {
            if (ReferenceEquals(active, operation)) Fault(operation);
            return true;
        }).ConfigureAwait(false);
    }

    private void Fault(Operation operation)
    {
        operation.Store.Fault();
        Publish(operation.Store.Current);
    }

    public async ValueTask DisposeAsync()
    {
        var pending = await inbox.CommandAsync(() =>
        {
            disposed = true;
            foreach (var operation in operations) operation.Invalidated = true;
            return operations.ToArray();
        }).ConfigureAwait(false);
        foreach (var operation in pending) _ = operation.RequestCancellation();
        await Task.WhenAll(pending.Select(operation => operation.Settled.Task)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // This lock owns cancellation-task identity only; accepted Review data above
    // is exclusively touched by inbox callbacks.
    private sealed class Operation(ReviewSessionPin session, ReviewOpenRequest request)
    {
        private readonly object lifecycle = new();
        private Task? cancellation;
        internal ReviewSessionPin Session { get; } = session;
        internal ReviewOpenRequest Request { get; } = request;
        internal ReviewStore Store { get; } = new(session, request);
        internal bool Invalidated { get; set; }
        internal bool Finished { get; set; }
        internal TaskCompletionSource<IReviewRun?> RunReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task RequestCancellation()
        {
            lock (lifecycle) return cancellation ??= Task.Run(async () =>
            {
                var run = await RunReady.Task.ConfigureAwait(false);
                if (run is not null) await run.CancelAsync().ConfigureAwait(false);
            });
        }
    }
}
