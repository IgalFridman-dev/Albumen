using System.Collections.Concurrent;
using System.Collections.Immutable;
using PhotoSift.Application.Action;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

// Commands and immutable projections only. Scope, Scan, Review and Action retain
// their sole owners; every presentation transition enters a non-inline inbox.
public sealed class PhotoSiftWorkflow : IPhotoSiftWorkflow
{
    private readonly IChooseScopeWorkflow scope;
    private readonly IScanCoordinator scan;
    private readonly IReviewCoordinator? review;
    private readonly IActionCoordinator? action;
    private readonly IActionRecoveryCoordinator? recovery;
    private readonly WorkflowInbox inbox;
    private PhotoSiftSnapshot current;
    private long nextRuleRevision;
    private ScanSnapshot? pendingScan;
    private int scanRefreshScheduled;
    private ReviewSnapshot? pendingReview;
    private int reviewRefreshScheduled;
    private ActionSnapshot? pendingAction;
    private int actionRefreshScheduled;
    private ActionRecoverySnapshot? pendingRecovery;
    private int recoveryRefreshScheduled;
    private ReviewSnapshot? approvedReviewProjection;
    private readonly object adapterLifecycle = new();
    private readonly List<Task> adapterTasks = [];
    private readonly ConcurrentQueue<Task> adapterFailures = new();
    private static readonly IEqualityComparer<EqualityEvidence> equalityComparison =
        EqualityComparer<EqualityEvidence>.Create(SameEquality,
            edge => HashCode.Combine(edge.Left, edge.LeftContent) ^ HashCode.Combine(edge.Right, edge.RightContent));
    private static readonly IEqualityComparer<CandidateGroup> candidateComparison =
        EqualityComparer<CandidateGroup>.Create(SameCandidate, group => group.GroupId.GetHashCode());

    public PhotoSiftWorkflow(IChooseScopeWorkflow scope, IScanCoordinator scan, IWorkflowScheduler scheduler,
        IReviewCoordinator? review = null, IActionCoordinator? action = null)
        : this(scope, scan, scheduler, review, action, null)
    {
    }

    public PhotoSiftWorkflow(IChooseScopeWorkflow scope, IScanCoordinator scan, IWorkflowScheduler scheduler,
        IReviewCoordinator? review, IActionCoordinator? action, IActionRecoveryCoordinator? recovery)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.scan = scan ?? throw new ArgumentNullException(nameof(scan));
        this.review = review;
        this.action = action;
        this.recovery = recovery;
        inbox = new(scheduler ?? throw new ArgumentNullException(nameof(scheduler)));
        current = new(scope.Current, null, EmptyScan(), WorkflowPhase.ChooseScope, InitialWorkflow.Create());
        scope.SnapshotChanged += OnScopeChanged;
        scan.SnapshotChanged += OnScanChanged;
        if (review is not null) review.SnapshotChanged += OnReviewChanged;
        if (action is not null) action.SnapshotChanged += OnActionChanged;
        if (recovery is not null)
        {
            recovery.SnapshotChanged += OnRecoveryChanged;
            RetainAdapterTask(ObserveRecoveryAdapterAsync(() => recovery.LoadAsync(CancellationToken.None)));
        }
    }

    public PhotoSiftSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<PhotoSiftSnapshot>? SnapshotChanged;

    public Task OpenReviewAsync() => ObserveCommandAsync(() => OpenReviewCoreAsync(navigate: true));

    private async Task OpenReviewCoreAsync(bool navigate, ScanPin? expectedScan = null)
    {
        while (true)
        {
            var request = await inbox.InvokeAsync(() =>
            {
                RefreshScope();
                var acceptedScan = scan.Current;
                if (review is null || acceptedScan.Status is not (ScanStatus.Complete or ScanStatus.Incomplete)
                    || (expectedScan is not null && expectedScan != acceptedScan.Pin)) return null;
                var request = CaptureReviewRequest(acceptedScan);
                if (request is null) return null;
                var acceptedReview = review.Current;
                if (!ReviewMatchesScan(acceptedReview, acceptedScan)) return request;
                if (navigate)
                    Publish(current with { Scan = acceptedScan, Review = acceptedReview,
                        CurrentPhase = WorkflowPhase.Review });
                return null;
            }).ConfigureAwait(false);
            if (request is null) return;
            var result = await review!.OpenAsync(request, CancellationToken.None).ConfigureAwait(false);
            var retry = await inbox.InvokeAsync(() =>
            {
                RefreshScope();
                var acceptedScan = scan.Current;
                if (current.Scope != request.Scope || current.Rules != request.Rules
                    || acceptedScan.Pin != request.Scan.Pin
                    || acceptedScan.Status is not (ScanStatus.Complete or ScanStatus.Incomplete)) return false;
                // ScanPin alone does not identify its candidate projection. A
                // same-pin update during admission is reconciled by the owner.
                if (!SameScan(acceptedScan, request.Scan)) return true;
                var acceptedReview = review.Current;
                if (result.Outcome != ReviewCommandOutcome.Committed || acceptedReview.Pin != result.Snapshot.Pin
                    || !ReviewMatchesScan(acceptedReview, acceptedScan)) return false;
                Publish(current with { Scan = acceptedScan, Review = acceptedReview,
                    CurrentPhase = navigate ? WorkflowPhase.Review : current.CurrentPhase });
                return false;
            }).ConfigureAwait(false);
            if (!retry) return;
            expectedScan = request.Scan.Pin;
        }
    }

    public Task ContinueAsync() => ObserveCommandAsync(() => inbox.InvokeAsync(() =>
    {
        RefreshScope();
        if (current.Scope.ScopeState == ScopeSessionState.Ready)
            Publish(current with { CurrentPhase = WorkflowPhase.ConfirmRules });
    }));

    public Task ConfirmExactRulesAsync() => ObserveCommandAsync(() => inbox.InvokeAsync(() =>
    {
        RefreshScope();
        if (current.CurrentPhase != WorkflowPhase.ConfirmRules || current.Scope.ScopeState != ScopeSessionState.Ready)
            return;
        if (current.Rules is null)
            Publish(current with { Rules = new(current.Scope.Revision, checked(++nextRuleRevision)) });
    }));

    public Task StartScanAsync() => ObserveCommandAsync(StartScanCoreAsync);

    private async Task StartScanCoreAsync()
    {
        var request = await inbox.InvokeAsync(() =>
        {
            RefreshScope();
            if (!ShellPhasePolicy.CanStart(current.Scope, current.Rules)
                || current.Scan.Status is ScanStatus.Checking or ScanStatus.Running) return null;
            Publish(current with { CurrentPhase = WorkflowPhase.Scan });
            return current;
        }).ConfigureAwait(false);
        // Command submission and fresh preflight belong to Scan; no work is
        // awaited while holding a shell turn.
        if (request is not null)
            await scan.StartAsync(request.Scope, request.Rules!, CancellationToken.None).ConfigureAwait(false);
    }

    public Task CancelScanAsync() => ObserveCommandAsync(CancelScanCoreAsync);

    public Task<ReviewCommandResult> ChangeReviewSelectionAsync(
        SelectionCommand command, CancellationToken cancellationToken) =>
        ObserveReviewCommandAsync(coordinator => coordinator.ChangeSelectionAsync(command, cancellationToken));

    public Task<ReviewCommandResult> AffirmReviewOverrideAsync(
        OverrideAffirmationCommand command, CancellationToken cancellationToken) =>
        ObserveReviewCommandAsync(coordinator => coordinator.AffirmOverrideAsync(command, cancellationToken));

    public Task<ReviewCommandResult> ApproveReviewAsync(
        ApproveSelectionCommand command, CancellationToken cancellationToken) =>
        ObserveReviewCommandAsync(async coordinator =>
        {
            var result = await coordinator.ApproveAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ReviewCommandOutcome.Approved
                && result.ApprovedRequest is { } request && action is not null)
            {
                await inbox.InvokeAsync(() =>
                {
                    // Capture disclosure only; approval is not admission or
                    // durable intent. Forward the owner's exact returned object.
                    if (result.Snapshot.Sealed && ReferenceEquals(result.Snapshot.ApprovedRequest, request))
                        approvedReviewProjection = result.Snapshot;
                }).ConfigureAwait(false);
                await action.ApproveAsync(request, cancellationToken).ConfigureAwait(false);
                // Completion can race the coalesced presentation notification.
                // Read the sole owner's current snapshot through the same inbox.
                await inbox.InvokeAsync(RefreshAction).ConfigureAwait(false);
            }
            return result;
        });

    public Task RevalidateUnknownAsync(Guid requestId, CancellationToken cancellationToken) =>
        ObserveCommandAsync(() => recovery?.RevalidateUnknownAsync(requestId, cancellationToken)
            ?? Task.CompletedTask);

    private async Task CancelScanCoreAsync()
    {
        await inbox.InvokeAsync(RefreshScope).ConfigureAwait(false);
        await scan.CancelAsync().ConfigureAwait(false);
    }

    public Task NavigateAsync(WorkflowPhase phase) => ObserveCommandAsync(() =>
        phase == WorkflowPhase.Review && review is not null
            ? OpenReviewCoreAsync(navigate: true)
            : inbox.InvokeAsync(() =>
            {
                RefreshScope();
                if (CanNavigate(current, phase)) Publish(current with { CurrentPhase = phase });
            }));

    private void OnScopeChanged(object? sender, ShellSnapshot snapshot)
    {
        // Submit invalidation at publication, before yielding the Scope owner.
        // This API only queues a Scan command; its work/cancellation is outside
        // both owner turns. Same-revision observations are neutral.
        if (!ReferenceEquals(snapshot, scope.Current)) return;
        var origin = review?.Current.Pin;
        if (snapshot.Revision != Current.Scope.Revision)
        {
            ObserveAdapter(() => scan.InvalidateAsync(snapshot.Revision, null), origin);
            if (review is not null) ObserveAdapter(() => review.InvalidateAsync(null), origin);
        }
        ObserveAdapter(() => inbox.InvokeAsync(RefreshScope), origin);
    }

    private void RefreshScope()
    {
        var accepted = scope.Current;
        if (accepted.Revision != current.Scope.Revision)
        {
            Publish(current with { Scope = accepted, Rules = null, Scan = EmptyScan(), Review = ReviewSnapshot.Closed, CurrentPhase = WorkflowPhase.ChooseScope });
        }
        else if (!ReferenceEquals(accepted, current.Scope))
        {
            Publish(current with { Scope = accepted });
        }
    }

    private void OnScanChanged(object? sender, ScanSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, scan.Current)) return;
        var liveReview = review?.Current;
        var replaceReview = liveReview is { Pin: { } reviewPin } && reviewPin.Scan == snapshot.Pin
            && snapshot.Status is ScanStatus.Complete or ScanStatus.Incomplete
            && !ReviewMatchesScan(liveReview, snapshot);
        // Submission occurs synchronously at the Scan publication boundary.
        // InvalidateAsync queues data acceptance before its first await.
        if (review is not null && snapshot.Pin != Current.Scan.Pin)
            ObserveAdapter(() => review.InvalidateAsync(snapshot.Pin), liveReview?.Pin);
        // Coalesce presentation notifications, not accepted results: the latest
        // immutable snapshot retains all terminal rows and evidence.
        Interlocked.Exchange(ref pendingScan, snapshot);
        ScheduleScanRefresh();
        if (replaceReview) ObserveAdapter(() => OpenReviewCoreAsync(navigate: false, snapshot.Pin), liveReview?.Pin);
    }

    // Called only in a workflow data turn; the returned values are immutable.
    private ReviewOpenRequest? CaptureReviewRequest(ScanSnapshot snapshot)
    {
        if (current.Rules is not { } rules || snapshot.Pin is not { } pin
            || pin.ScopeRevision != current.Scope.Revision || pin.RuleSetRevision != rules.RuleSetRevision) return null;
        return new ReviewOpenRequest(current.Scope, rules, snapshot);
    }

    private void OnReviewChanged(object? sender, ReviewSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, review?.Current)) return;
        Interlocked.Exchange(ref pendingReview, snapshot);
        ScheduleReviewRefresh();
    }

    private void OnActionChanged(object? sender, ActionSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, action?.Current)) return;
        Interlocked.Exchange(ref pendingAction, snapshot);
        ScheduleActionRefresh();
    }

    private void OnRecoveryChanged(object? sender, ActionRecoverySnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, recovery?.Current)) return;
        Interlocked.Exchange(ref pendingRecovery, snapshot);
        if (Interlocked.CompareExchange(ref recoveryRefreshScheduled, 1, 0) == 0)
            RetainAdapterTask(ObserveRecoveryAdapterAsync(ApplyRecoveryAsync));
    }

    private async Task ApplyRecoveryAsync()
    {
        await inbox.InvokeAsync(() =>
        {
            try
            {
                Interlocked.Exchange(ref pendingRecovery, null);
                var snapshot = recovery?.Current;
                if (snapshot is not null && !ReferenceEquals(snapshot, current.ActionRecovery))
                    Publish(current with { ActionRecovery = snapshot });
            }
            finally
            {
                Volatile.Write(ref recoveryRefreshScheduled, 0);
                if (Volatile.Read(ref pendingRecovery) is not null
                    && Interlocked.CompareExchange(ref recoveryRefreshScheduled, 1, 0) == 0)
                    RetainAdapterTask(ObserveRecoveryAdapterAsync(ApplyRecoveryAsync));
            }
        }).ConfigureAwait(false);
    }

    private void ScheduleActionRefresh()
    {
        if (Interlocked.CompareExchange(ref actionRefreshScheduled, 1, 0) == 0)
            ObserveActionAdapter(ApplyActionAsync, action?.Current.RequestId);
    }

    private async Task ApplyActionAsync()
    {
        await inbox.InvokeAsync(() =>
        {
            try
            {
                Interlocked.Exchange(ref pendingAction, null);
                RefreshAction();
            }
            finally
            {
                Volatile.Write(ref actionRefreshScheduled, 0);
                if (Volatile.Read(ref pendingAction) is not null) ScheduleActionRefresh();
            }
        }).ConfigureAwait(false);
    }

    private void RefreshAction()
    {
        var snapshot = action?.Current;
        if (snapshot is null || ReferenceEquals(snapshot, current.Action)) return;
        var disclosure = current.ActionReview;
        if (disclosure.ApprovedRequest?.RequestId != snapshot.RequestId)
        {
            disclosure = approvedReviewProjection is { Sealed: true, ApprovedRequest: { } approved }
                && approved.RequestId == snapshot.RequestId ? approvedReviewProjection
                : review?.Current is { Sealed: true, ApprovedRequest: { } liveApproved } live
                    && liveApproved.RequestId == snapshot.RequestId ? live : ReviewSnapshot.Closed;
        }
        var newlyAdmitted = snapshot.RequestId is not null && snapshot.RequestId != current.Action.RequestId;
        Publish(current with { Action = snapshot, ActionReview = disclosure,
            CurrentPhase = newlyAdmitted ? WorkflowPhase.ApproveCleanup : current.CurrentPhase }, snapshot.RequestId);
    }

    private void ScheduleReviewRefresh()
    {
        if (Interlocked.CompareExchange(ref reviewRefreshScheduled, 1, 0) == 0)
            ObserveAdapter(ApplyReviewAsync, review?.Current.Pin);
    }

    private async Task ApplyReviewAsync()
    {
        await inbox.InvokeAsync(() =>
        {
            try
            {
                Interlocked.Exchange(ref pendingReview, null);
                var snapshot = review?.Current;
                RefreshScope();
                if (snapshot is null) return;
                if (snapshot.Pin is { } pin && (!ReviewMatchesScan(snapshot, scan.Current) || current.Rules is null
                    || pin.Scan.ScopeRevision != current.Scope.Revision || pin.Scan.RuleSetRevision != current.Rules.RuleSetRevision)) return;
                Publish(current with { Review = snapshot });
            }
            finally
            {
                Volatile.Write(ref reviewRefreshScheduled, 0);
                if (Volatile.Read(ref pendingReview) is not null) ScheduleReviewRefresh();
            }
        }).ConfigureAwait(false);
    }

    private void ScheduleScanRefresh()
    {
        if (Interlocked.CompareExchange(ref scanRefreshScheduled, 1, 0) == 0)
            ObserveAdapter(ApplyScanAsync, review?.Current.Pin);
    }

    private async Task ApplyScanAsync()
    {
        await inbox.InvokeAsync(() =>
        {
            try
            {
                Interlocked.Exchange(ref pendingScan, null);
                var snapshot = scan.Current;
                RefreshScope();
                if (snapshot?.Pin is not { } pin || current.Rules is not { } rules
                    || pin.ScopeRevision != scope.Current.Revision
                    || pin.ScopeRevision != rules.ScopeRevision
                    || pin.RuleSetRevision != rules.RuleSetRevision) return;
                Publish(current with { Scan = snapshot });
            }
            finally
            {
                Volatile.Write(ref scanRefreshScheduled, 0);
                if (Volatile.Read(ref pendingScan) is not null) ScheduleScanRefresh();
            }
        }).ConfigureAwait(false);
    }

    private static bool ReviewMatchesScan(ReviewSnapshot reviewSnapshot, ScanSnapshot scanSnapshot) =>
        reviewSnapshot.Pin?.Scan == scanSnapshot.Pin && reviewSnapshot.Coverage == scanSnapshot.Status
        && !scanSnapshot.Groups.IsDefault
        && SameValues(reviewSnapshot.Groups.Select(group => group.Candidate), scanSnapshot.Groups, candidateComparison);

    private static bool SameScan(ScanSnapshot left, ScanSnapshot right) =>
        // Record equality still compares every scalar fact. Only collection
        // identity is replaced by value comparison below.
        left with { Occurrences = right.Occurrences, Groups = right.Groups } == right
        && SameArray(left.Occurrences, right.Occurrences)
        && SameArray(left.Groups, right.Groups, candidateComparison);

    private static bool SameCandidate(CandidateGroup? left, CandidateGroup? right) =>
        left is not null && right is not null
        && left with { Members = right.Members, Equality = right.Equality } == right
        && SameArray(left.Members, right.Members)
        && SameArray(left.Equality, right.Equality, equalityComparison);

    private static bool SameEquality(EqualityEvidence? left, EqualityEvidence? right) =>
        left == right || (left is not null && right is not null
            && left.Left == right.Right && left.Right == right.Left
            && left.LeftContent == right.RightContent && left.RightContent == right.LeftContent);

    private static bool SameArray<T>(ImmutableArray<T> left, ImmutableArray<T> right,
        IEqualityComparer<T>? comparison = null) where T : notnull =>
        left.IsDefault || right.IsDefault ? left.IsDefault && right.IsDefault : SameValues(left, right, comparison);

    private static bool SameValues<T>(IEnumerable<T> left, IEnumerable<T> right,
        IEqualityComparer<T>? comparison = null) where T : notnull
    {
        // Group membership and equality edges are unordered, but multiplicity
        // is significant: a malformed duplicate cannot disappear in a set.
        var counts = new Dictionary<T, int>(comparison);
        foreach (var value in left) counts[value] = counts.GetValueOrDefault(value) + 1;
        foreach (var value in right)
        {
            if (!counts.TryGetValue(value, out var count)) return false;
            if (count == 1) counts.Remove(value);
            else counts[value] = count - 1;
        }
        return counts.Count == 0;
    }

    private async Task ObserveCommandAsync(Func<Task> command)
    {
        // Diagnostics never gate the command (especially cancellation). A
        // command exception wins and leaves queued diagnostics for later calls.
        await command().ConfigureAwait(false);
        await ConsumeNextAdapterFailureAsync().ConfigureAwait(false);
    }

    private Task ConsumeNextAdapterFailureAsync() => adapterFailures.TryDequeue(out var failure) ? failure : Task.CompletedTask;

    private async Task<ReviewCommandResult> ObserveReviewCommandAsync(
        Func<IReviewCoordinator, Task<ReviewCommandResult>> command)
    {
        if (review is null) return new(ReviewCommandOutcome.Invalid, Current.Review);
        var result = await command(review).ConfigureAwait(false);
        await ConsumeNextAdapterFailureAsync().ConfigureAwait(false);
        return result;
    }

    private void ObserveAdapter(Func<Task> work, ReviewSessionPin? origin)
    {
        var observation = ObserveAdapterAsync(work, origin);
        RetainAdapterTask(observation);
    }

    private void ObserveActionAdapter(Func<Task> work, Guid? origin)
    {
        RetainAdapterTask(ObserveActionAdapterAsync(work, origin));
    }

    private void RetainAdapterTask(Task observation)
    {
        // This lock retains asynchronous supervision only. It never owns or
        // gates Review data, and commands do not wait for pending adapter work.
        lock (adapterLifecycle)
        {
            adapterTasks.RemoveAll(task => task.IsCompleted);
            adapterTasks.Add(observation);
        }
    }

    private async Task ObserveActionAdapterAsync(Func<Task> work, Guid? origin)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception exception)
        {
            RetainAdapterFailure(exception);
            if (origin is not null && action is not null)
            {
                try { await action.ReportObserverFaultAsync(origin.Value).ConfigureAwait(false); }
                catch (Exception reportingFailure) { RetainAdapterFailure(reportingFailure); }
            }
        }
    }

    private async Task ObserveRecoveryAdapterAsync(Func<Task> work)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception exception) { RetainAdapterFailure(exception); }
    }

    private async Task ObserveAdapterAsync(Func<Task> work, ReviewSessionPin? origin)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception exception)
        {
            RetainAdapterFailure(exception);
            if (origin is not null && review is not null)
            {
                try { await review.ReportObserverFaultAsync(origin).ConfigureAwait(false); }
                catch (Exception reportingFailure) { RetainAdapterFailure(reportingFailure); }
            }
        }
    }

    private void RetainAdapterFailure(Exception exception)
    {
        var failure = Task.FromException(exception);
        // Observe now, retain for the existing Task-returning command surface.
        // Failures without a live session cannot be attached to a newer Review.
        _ = failure.Exception;
        // FIFO arrival, exactly one delivery per successful command completion.
        // Concurrent commands claim distinct diagnostics atomically.
        adapterFailures.Enqueue(failure);
    }

    private static bool CanNavigate(PhotoSiftSnapshot snapshot, WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.ChooseScope => true,
        WorkflowPhase.ConfirmRules => snapshot.Scope.ScopeState == ScopeSessionState.Ready
            && (snapshot.CurrentPhase != WorkflowPhase.ChooseScope || snapshot.Rules is not null),
        WorkflowPhase.Scan => ShellPhasePolicy.CanStart(snapshot.Scope, snapshot.Rules),
        WorkflowPhase.Review => snapshot.Scan.Pin is { } pin && snapshot.Rules is { } rules
            && pin.ScopeRevision == snapshot.Scope.Revision && pin.RuleSetRevision == rules.RuleSetRevision,
        WorkflowPhase.ApproveCleanup => snapshot.Action.RequestId is not null,
        _ => false,
    };

    private void Publish(PhotoSiftSnapshot snapshot, Guid? actionOrigin = null)
    {
        var phases = InitialWorkflow.Create().Select(phase => phase with
        {
            Availability = phase.Phase == snapshot.CurrentPhase ? PhaseAvailability.Current
                : CanNavigate(snapshot, phase.Phase) ? PhaseAvailability.Available : PhaseAvailability.Unavailable,
        }).ToImmutableArray();
        var published = snapshot with { Phases = phases };
        Volatile.Write(ref current, published);
        foreach (var observer in SnapshotChanged?.GetInvocationList() ?? [])
        {
            try { ((EventHandler<PhotoSiftSnapshot>)observer)(this, published); }
            catch (Exception exception)
            {
                // The exact published session is the failure's origin, even if
                // a subscriber synchronously queues a replacement before throwing.
                if (actionOrigin is { } requestId)
                    ObserveActionAdapter(() => Task.FromException(exception), requestId);
                else
                    ObserveAdapter(() => Task.FromException(exception), published.Review.Pin);
            }
        }
    }

    private static ScanSnapshot EmptyScan() => new(null, ScanStatus.Idle,
        new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0));
}
