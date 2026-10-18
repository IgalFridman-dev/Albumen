using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

public sealed partial class ReviewCoordinator : IReviewCoordinator, IReviewResultSink, IApprovedActionAuthority
{
    private readonly ReviewOwnerInbox inbox;
    private readonly IReviewRevalidationExecutor revalidation;
    private readonly IOverrideEvidenceStore evidenceStore;
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
        IOverrideEvidenceStore evidenceStore, int resultCapacity = 16,
        ProtectedActionSafetyValidator? actionSafety = null)
    {
        inbox = new(scheduler, resultCapacity);
        inbox.DeliverAfterTurn = DeliverPublications;
        this.revalidation = revalidation ?? throw new ArgumentNullException(nameof(revalidation));
        this.evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        this.actionSafety = actionSafety;
    }
    public ReviewSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<ReviewSnapshot>? SnapshotChanged;

    public async Task ReportObserverFaultAsync(ReviewSessionPin session) =>
        await inbox.CommandAsync(() =>
        {
            if (!disposed && active is { Invalidated: false } operation && operation.Session == session
                && current.Pin == session && current.Status != ReviewStatus.Faulted) Fault(operation);
            return true;
        }).ConfigureAwait(false);

    public async Task<ReviewCommandResult> OpenAsync(ReviewOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transition = await inbox.CommandAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidOpen(request))
            {
                var invalidated = active is { } existing
                    && request.Scan.Pin is { } requestedPin
                    && existing.Request.Scan.Pin == requestedPin
                        ? existing
                        : null;
                if (invalidated is not null)
                {
                    invalidated.Invalidated = true;
                    QueueActionCancellation(invalidated);
                    revoked.Add(invalidated.Request.Scan.Pin!);
                    admissionRestricted = true;
                    requiredScan = null;
                    active = null;
                    Publish(ReviewSnapshot.Closed);
                }
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Invalid, current),
                    Next: (Operation?)null, Old: invalidated);
            }
            var pin = request.Scan.Pin!;
            if (IsOlder(pin) || revoked.Contains(pin) || (admissionRestricted && pin != requiredScan)
                || request.Scan.Groups.Any(g => retiredGroups.Contains(g.GroupId)
                    || (groupRevisions.TryGetValue(g.GroupId, out var revision) && g.GroupRevision < revision))
                || (request.ContinuesRequestId is { } continues && !ContinuesSealedReview(continues, pin)))
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Stale, current), Next: (Operation?)null, Old: (Operation?)null);
            var old = active;
            if (old is not null)
            {
                old.Invalidated = true;
                QueueActionCancellation(old);
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

    public async Task<ReviewCommandResult> AffirmOverrideAsync(
        OverrideAffirmationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var start = await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || active is null || active.Invalidated)
            {
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Stale, current),
                    Operation: (Operation?)null, Attempt: (OverridePersistenceAttempt?)null);
            }

            try
            {
                var begun = active.Store.BeginAffirmation(command);
                return (begun.Result, Operation: begun.Attempt is null ? null : active, begun.Attempt);
            }
            catch (Exception)
            {
                Fault(active);
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Faulted, current),
                    Operation: (Operation?)null, Attempt: (OverridePersistenceAttempt?)null);
            }
        }).ConfigureAwait(false);
        if (start.Operation is not { } operation || start.Attempt is not { } attempt)
        {
            return start.Result;
        }

        OverridePersistResult persisted;
        try
        {
            persisted = await evidenceStore.PersistAsync(attempt.Evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await inbox.CommandAsync(() =>
            {
                operation.Store.AbandonAffirmation(attempt);
                return true;
            }).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            return await inbox.CommandAsync(() =>
            {
                operation.Store.AbandonAffirmation(attempt);
                if (disposed || !ReferenceEquals(active, operation) || operation.Invalidated)
                {
                    return new ReviewCommandResult(ReviewCommandOutcome.Stale, current);
                }

                Fault(operation);
                return new ReviewCommandResult(ReviewCommandOutcome.Faulted, current);
            }).ConfigureAwait(false);
        }

        return await inbox.CommandAsync(() =>
        {
            if (disposed || !ReferenceEquals(active, operation) || operation.Invalidated)
            {
                operation.Store.AbandonAffirmation(attempt);
                return new ReviewCommandResult(ReviewCommandOutcome.Stale, current);
            }

            try
            {
                var result = operation.Store.CompleteAffirmation(attempt, persisted);
                if (!ReferenceEquals(result.Snapshot, current))
                {
                    Publish(result.Snapshot);
                }

                return result;
            }
            catch (Exception)
            {
                Fault(operation);
                return new ReviewCommandResult(ReviewCommandOutcome.Faulted, current);
            }
        }).ConfigureAwait(false);
    }

    public async Task<ReviewCommandResult> ApproveAsync(
        ApproveSelectionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var start = await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || active is null || active.Invalidated)
            {
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Stale, current),
                    Operation: (Operation?)null, Attempt: (ApprovalAttempt?)null);
            }

            try
            {
                var begun = active.Store.BeginApproval(command);
                return (begun.Result, Operation: begun.Attempt is null ? null : active,
                    begun.Attempt);
            }
            catch (Exception)
            {
                Fault(active);
                return (Result: new ReviewCommandResult(ReviewCommandOutcome.Faulted, current),
                    Operation: (Operation?)null, Attempt: (ApprovalAttempt?)null);
            }
        }).ConfigureAwait(false);
        if (start.Operation is not { } operation || start.Attempt is not { } attempt)
        {
            return start.Result;
        }

        var reads = ImmutableArray.CreateBuilder<OverrideReadResult>(
            attempt.ExpectedEvidence.Length);
        foreach (var expected in attempt.ExpectedEvidence)
        {
            try
            {
                reads.Add(await evidenceStore.ReadAsync(expected.EvidenceId,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                reads.Add(new(OverrideReadOutcome.Unavailable, null));
            }
        }

        return await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || !ReferenceEquals(active, operation) || operation.Invalidated)
            {
                return new ReviewCommandResult(ReviewCommandOutcome.Stale, current);
            }

            try
            {
                var result = operation.Store.CompleteApproval(attempt, reads.ToImmutable());
                if (!ReferenceEquals(result.Snapshot, current))
                {
                    Publish(result.Snapshot);
                }

                return result;
            }
            catch (Exception)
            {
                Fault(operation);
                return new ReviewCommandResult(ReviewCommandOutcome.Faulted, current);
            }
        }).ConfigureAwait(false);
    }

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
                QueueActionCancellation(previous);
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

    // PM-013: a continuation replaces only the sealed review of the cleanup it
    // names, and only once no claim or safety refresh still holds that review.
    // Whether the cleanup itself finished (Complete/Incomplete, not Faulted or still running)
    // is checked by the workflow before it asks to continue; here only the sealed review matters.
    private bool ContinuesSealedReview(Guid requestId, ScanPin pin) =>
        active is { Invalidated: false, ClaimAttempt: null } operation
        && operation.Request.Scan.Pin == pin
        && operation.Store.Current is { Sealed: true, ApprovedRequest: { } approved } snapshot
        && snapshot.Status != ReviewStatus.Faulted
        && approved.RequestId == requestId
        && !actionEffects.Values.Any(effect => ReferenceEquals(effect.Operation, operation));

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
            || request.Rules.Options is not { IsValid: true }
            || request.Scan.Groups.IsDefault || request.Scan.Occurrences.IsDefault
            || request.ExcludedOccurrenceIds is null || request.ClosedGroupIds is null
            || request.ContinuesRequestId == Guid.Empty
            || (request.ContinuesRequestId is null
                && (request.ExcludedOccurrenceIds.Count > 0 || request.ClosedGroupIds.Count > 0))) return false;
        var occurrences = new Dictionary<long, ScanFileOccurrence>();
        foreach (var occurrence in request.Scan.Occurrences)
            if (!occurrences.TryAdd(occurrence.Token.OccurrenceId, occurrence)) return false;
        var groups = new HashSet<Guid>();
        var memberIds = new HashSet<long>();
        foreach (var group in request.Scan.Groups)
        {
            if (group.Pin != pin || group.GroupId == Guid.Empty || !groups.Add(group.GroupId)
                || group.Members.IsDefault || group.Members.Length < 2
                || group.Equality.IsDefault || group.Similarity.IsDefault
                || group.KeeperOccurrenceId is not null || !Enum.IsDefined(group.Category)
                || request.Rules.Options.Near != NearDuplicateMode.Conservative
                    && (group.Category is CandidateCategory.NearDuplicate or CandidateCategory.Uncertain)) return false;
            var members = new Dictionary<long, ScanFileOccurrence>();
            foreach (var member in group.Members)
            {
                if (member.Token.Pin != pin || member.Token.OccurrenceRevision <= 0 || member.Token.Attempt <= 0
                    || !members.TryAdd(member.Token.OccurrenceId, member)
                    || !occurrences.TryGetValue(member.Token.OccurrenceId, out var occurrence)
                    || occurrence != member || member.Content is null || member.PhysicalFileKey is null
                    || member.Content.PhysicalFileKey != member.PhysicalFileKey
                    || member.Enumerate.State != ScanStageState.Succeeded
                    || member.Exact.State != ScanStageState.Succeeded) return false;
                memberIds.Add(member.Token.OccurrenceId);
            }
            if (!ValidCategoryEvidence(group, members)) return false;
        }
        return request.ClosedGroupIds.IsSubsetOf(groups) && request.ExcludedOccurrenceIds.IsSubsetOf(memberIds);
    }

    private static bool ValidCategoryEvidence(
        CandidateGroup group,
        IReadOnlyDictionary<long, ScanFileOccurrence> members) => group.Category switch
    {
        CandidateCategory.Exact => !group.Uncertain && group.Similarity.IsEmpty
            && ValidEquality(group, members),
        CandidateCategory.NearDuplicate => !group.Uncertain && group.Equality.IsEmpty
            && ValidSimilarity(group, members, NearRelationshipKind.Near),
        CandidateCategory.Uncertain => group.Uncertain && group.Equality.IsEmpty
            && ValidSimilarity(group, members, NearRelationshipKind.Uncertain),
        _ => false,
    };

    private static bool ValidEquality(
        CandidateGroup group,
        IReadOnlyDictionary<long, ScanFileOccurrence> members)
    {
        if (group.Equality.IsEmpty) return false;
        var physical = group.Members.Select(member => member.PhysicalFileKey!.Value)
            .Distinct().ToArray();
        if (physical.Length < 2) return false;
        var connected = new Dictionary<PhysicalFileKey, HashSet<PhysicalFileKey>>();
        foreach (var key in physical) connected.Add(key, []);
        var edges = new HashSet<(long Left, long Right)>();
        foreach (var edge in group.Equality)
        {
            if (!members.TryGetValue(edge.Left.OccurrenceId, out var left)
                || !members.TryGetValue(edge.Right.OccurrenceId, out var right)
                || edge.Left != left.Token || edge.Right != right.Token
                || edge.LeftContent != left.Content || edge.RightContent != right.Content
                || edge.LeftContent.PhysicalFileKey == edge.RightContent.PhysicalFileKey
                || edge.LeftContent.ByteLength != edge.RightContent.ByteLength
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    edge.LeftContent.Sha256, edge.RightContent.Sha256)) return false;
            var pair = edge.Left.OccurrenceId < edge.Right.OccurrenceId
                ? (edge.Left.OccurrenceId, edge.Right.OccurrenceId)
                : (edge.Right.OccurrenceId, edge.Left.OccurrenceId);
            if (!edges.Add(pair)) return false;
            connected[edge.LeftContent.PhysicalFileKey].Add(edge.RightContent.PhysicalFileKey);
            connected[edge.RightContent.PhysicalFileKey].Add(edge.LeftContent.PhysicalFileKey);
        }
        return Connected(physical[0], connected) == physical.Length;
    }

    private static bool ValidSimilarity(
        CandidateGroup group,
        IReadOnlyDictionary<long, ScanFileOccurrence> members,
        NearRelationshipKind expectedKind)
    {
        if (group.Similarity.IsEmpty || group.Members.Any(member =>
                member.VisualFeatures.State != ScanStageState.Succeeded
                || member.NearDuplicate.State != ScanStageState.Succeeded)) return false;
        var connected = members.Keys.ToDictionary(id => id, _ => new HashSet<long>());
        var edges = new HashSet<(long Left, long Right)>();
        var hasExpectedKind = false;
        foreach (var edge in group.Similarity)
        {
            if (!members.TryGetValue(edge.Left.OccurrenceId, out var left)
                || !members.TryGetValue(edge.Right.OccurrenceId, out var right)
                || edge.Left != left.Token || edge.Right != right.Token
                || edge.Left == edge.Right || !AllowedKind(edge.Kind, expectedKind)
                || SameBytes(left.Content!, right.Content!)
                || !ValidDistance(edge, edge.Kind)) return false;
            hasExpectedKind |= edge.Kind == expectedKind;
            var pair = edge.Left.OccurrenceId < edge.Right.OccurrenceId
                ? (edge.Left.OccurrenceId, edge.Right.OccurrenceId)
                : (edge.Right.OccurrenceId, edge.Left.OccurrenceId);
            if (!edges.Add(pair)) return false;
            connected[pair.Item1].Add(pair.Item2);
            connected[pair.Item2].Add(pair.Item1);
        }
        return hasExpectedKind
            && Connected(group.Members[0].Token.OccurrenceId, connected) == members.Count;
    }

    // A look-alike set is Uncertain as a whole but keeps the Near evidence between its
    // closest members; a Near group holds Near evidence only.
    private static bool AllowedKind(NearRelationshipKind kind, NearRelationshipKind expectedKind) =>
        kind == expectedKind
        || expectedKind == NearRelationshipKind.Uncertain && kind == NearRelationshipKind.Near;

    private static bool ValidDistance(NearDuplicateEvidence edge, NearRelationshipKind kind)
    {
        if (edge.HammingDistance < 0 || edge.ColorDistance < 0) return false;
        var near = edge.HammingDistance <= NearDuplicateThresholds.NearHammingMaximum
            && edge.ColorDistance <= NearDuplicateThresholds.NearColorMaximum;
        return kind == NearRelationshipKind.Near ? near
            : !near && edge.HammingDistance <= NearDuplicateThresholds.UncertainHammingMaximum
                && edge.ColorDistance <= NearDuplicateThresholds.UncertainColorMaximum;
    }

    private static bool SameBytes(ContentRevision left, ContentRevision right) =>
        left.ByteLength == right.ByteLength
        && StringComparer.OrdinalIgnoreCase.Equals(left.Sha256, right.Sha256);

    private static int Connected<T>(T start, IReadOnlyDictionary<T, HashSet<T>> graph)
        where T : notnull
    {
        var visited = new HashSet<T> { start };
        var pending = new Queue<T>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
            foreach (var peer in graph[current])
                if (visited.Add(peer)) pending.Enqueue(peer);
        return visited.Count;
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
                    new(operation.Session.Scan, operation.Request.Scope.Preflight!.Scope!), operation.Store.RunGroups, this),
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
            QueueActionCancellation(operation);
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
        if (snapshot.Status == ReviewStatus.Faulted && active is { } operation) QueueActionCancellation(operation);
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
        DeliverActionCancellations();
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
            foreach (var effect in actionEffects.Values) actionCancellations.Enqueue(effect);
            return (Operations: operations.ToArray(), ActionEffects: actionEffects.Keys.ToArray());
        }).ConfigureAwait(false);
        foreach (var operation in pending.Operations) _ = operation.RequestCancellation();
        await Task.WhenAll(pending.Operations.Select(operation => operation.Settled.Task)
            .Concat(pending.ActionEffects)).ConfigureAwait(false);
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
        internal ActionClaimAttempt? ClaimAttempt { get; set; }
        internal ApprovedActionClaim? Claim { get; set; }
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
