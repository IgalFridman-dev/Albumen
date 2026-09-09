using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

// Navigation and rule confirmation only. Scope and Scan remain owned by their
// existing workflows; every observer/command transition enters a non-inline inbox.
public sealed class PhotoSiftWorkflow : IPhotoSiftWorkflow
{
    private readonly IChooseScopeWorkflow scope;
    private readonly IScanCoordinator scan;
    private readonly IReviewCoordinator? review;
    private readonly WorkflowInbox inbox;
    private PhotoSiftSnapshot current;
    private long nextRuleRevision;
    private ScanSnapshot? pendingScan;
    private int scanRefreshScheduled;
    private ReviewSnapshot? pendingReview;
    private int reviewRefreshScheduled;

    public PhotoSiftWorkflow(IChooseScopeWorkflow scope, IScanCoordinator scan, IWorkflowScheduler scheduler,
        IReviewCoordinator? review = null)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.scan = scan ?? throw new ArgumentNullException(nameof(scan));
        this.review = review;
        inbox = new(scheduler ?? throw new ArgumentNullException(nameof(scheduler)));
        current = new(scope.Current, null, EmptyScan(), WorkflowPhase.ChooseScope, InitialWorkflow.Create());
        scope.SnapshotChanged += OnScopeChanged;
        scan.SnapshotChanged += OnScanChanged;
        if (review is not null) review.SnapshotChanged += OnReviewChanged;
    }

    public PhotoSiftSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<PhotoSiftSnapshot>? SnapshotChanged;

    public async Task OpenReviewAsync()
    {
        var request = await inbox.InvokeAsync(() =>
        {
            RefreshScope();
            if (review is null || current.Scan.Status is not (ScanStatus.Complete or ScanStatus.Incomplete)) return null;
            return CaptureReviewRequest(current.Scan);
        }).ConfigureAwait(false);
        if (request is null) return;
        var result = await review!.OpenAsync(request, CancellationToken.None).ConfigureAwait(false);
        await inbox.InvokeAsync(() =>
        {
            RefreshScope();
            if (result.Outcome != ReviewCommandOutcome.Committed || current.Scope.Revision != request.Scope.Revision
                || current.Rules != request.Rules || current.Scan.Pin != request.Scan.Pin
                || review.Current.Pin != result.Snapshot.Pin) return;
            Publish(current with { Review = review.Current, CurrentPhase = WorkflowPhase.Review });
        }).ConfigureAwait(false);
    }

    public Task ContinueAsync() => inbox.InvokeAsync(() =>
    {
        RefreshScope();
        if (current.Scope.ScopeState == ScopeSessionState.Ready)
            Publish(current with { CurrentPhase = WorkflowPhase.ConfirmRules });
    });

    public Task ConfirmExactRulesAsync() => inbox.InvokeAsync(() =>
    {
        RefreshScope();
        if (current.CurrentPhase != WorkflowPhase.ConfirmRules || current.Scope.ScopeState != ScopeSessionState.Ready)
            return;
        if (current.Rules is null)
            Publish(current with { Rules = new(current.Scope.Revision, checked(++nextRuleRevision)) });
    });

    public async Task StartScanAsync()
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

    public async Task CancelScanAsync()
    {
        await inbox.InvokeAsync(RefreshScope).ConfigureAwait(false);
        await scan.CancelAsync().ConfigureAwait(false);
    }

    public Task NavigateAsync(WorkflowPhase phase) => inbox.InvokeAsync(() =>
    {
        RefreshScope();
        if (CanNavigate(current, phase)) Publish(current with { CurrentPhase = phase });
    });

    private async void OnScopeChanged(object? sender, ShellSnapshot snapshot)
    {
        // Submit invalidation at publication, before yielding the Scope owner.
        // This API only queues a Scan command; its work/cancellation is outside
        // both owner turns. Same-revision observations are neutral.
        var invalidation = snapshot.Revision != Current.Scope.Revision
            ? scan.InvalidateAsync(snapshot.Revision, null)
            : Task.CompletedTask;
        var reviewInvalidation = snapshot.Revision != Current.Scope.Revision && review is not null
            ? review.InvalidateAsync(null) : Task.CompletedTask;
        await inbox.InvokeAsync(RefreshScope).ConfigureAwait(false);
        await Task.WhenAll(invalidation, reviewInvalidation).ConfigureAwait(false);
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
        var liveReview = review?.Current;
        var replaceReview = liveReview is { Pin: { } reviewPin } && reviewPin.Scan == snapshot.Pin
            && snapshot.Status is ScanStatus.Complete or ScanStatus.Incomplete
            && (liveReview.Groups.Length != snapshot.Groups.Length || liveReview.Groups.Any(g =>
                !snapshot.Groups.Any(candidate => candidate.GroupId == g.Candidate.GroupId
                    && candidate.GroupRevision == g.Candidate.GroupRevision)));
        // Submission occurs synchronously at the Scan publication boundary.
        // InvalidateAsync queues data acceptance before its first await.
        if (review is not null && snapshot.Pin != Current.Scan.Pin)
            ObserveReviewInvalidationAsync(review.InvalidateAsync(snapshot.Pin));
        // Coalesce presentation notifications, not accepted results: the latest
        // immutable snapshot retains all terminal rows and evidence.
        Interlocked.Exchange(ref pendingScan, snapshot);
        ScheduleScanRefresh();
        if (replaceReview) ReplaceReviewAsync(snapshot);
    }

    private async void ReplaceReviewAsync(ScanSnapshot expected)
    {
        var request = await inbox.InvokeAsync(() =>
        {
            RefreshScope();
            return current.Scan == expected ? CaptureReviewRequest(expected) : null;
        }).ConfigureAwait(false);
        if (request is not null) await review!.OpenAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    // Called only in a workflow data turn; the returned values are immutable.
    private ReviewOpenRequest? CaptureReviewRequest(ScanSnapshot snapshot)
    {
        if (current.Rules is not { } rules || snapshot.Pin is not { } pin
            || pin.ScopeRevision != current.Scope.Revision || pin.RuleSetRevision != rules.RuleSetRevision) return null;
        return new ReviewOpenRequest(current.Scope, rules, snapshot);
    }

    private static async void ObserveReviewInvalidationAsync(Task invalidation) => await invalidation.ConfigureAwait(false);

    private void OnReviewChanged(object? sender, ReviewSnapshot snapshot)
    {
        Interlocked.Exchange(ref pendingReview, snapshot);
        ScheduleReviewRefresh();
    }

    private void ScheduleReviewRefresh()
    {
        if (Interlocked.CompareExchange(ref reviewRefreshScheduled, 1, 0) == 0) ApplyReviewAsync();
    }

    private async void ApplyReviewAsync()
    {
        await inbox.InvokeAsync(() =>
        {
            try
            {
                var snapshot = Interlocked.Exchange(ref pendingReview, null);
                RefreshScope();
                if (snapshot is null) return;
                if (snapshot.Pin is { } pin && (pin.Scan != current.Scan.Pin || current.Rules is null
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
            ApplyScanAsync();
    }

    private async void ApplyScanAsync()
    {
        await inbox.InvokeAsync(() =>
        {
            try
            {
                var snapshot = Interlocked.Exchange(ref pendingScan, null);
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

    private static bool CanNavigate(PhotoSiftSnapshot snapshot, WorkflowPhase phase) => phase switch
    {
        WorkflowPhase.ChooseScope => true,
        WorkflowPhase.ConfirmRules => snapshot.Scope.ScopeState == ScopeSessionState.Ready
            && (snapshot.CurrentPhase != WorkflowPhase.ChooseScope || snapshot.Rules is not null),
        WorkflowPhase.Scan => ShellPhasePolicy.CanStart(snapshot.Scope, snapshot.Rules),
        WorkflowPhase.Review => snapshot.Scan.Pin is { } pin && snapshot.Rules is { } rules
            && pin.ScopeRevision == snapshot.Scope.Revision && pin.RuleSetRevision == rules.RuleSetRevision,
        _ => false,
    };

    private void Publish(PhotoSiftSnapshot snapshot)
    {
        var phases = InitialWorkflow.Create().Select(phase => phase with
        {
            Availability = phase.Phase == snapshot.CurrentPhase ? PhaseAvailability.Current
                : CanNavigate(snapshot, phase.Phase) ? PhaseAvailability.Available : PhaseAvailability.Unavailable,
        }).ToImmutableArray();
        var published = snapshot with { Phases = phases };
        Volatile.Write(ref current, published);
        SnapshotChanged?.Invoke(this, published);
    }

    private static ScanSnapshot EmptyScan() => new(null, ScanStatus.Idle,
        new(ScanStageState.Pending, ScanReason.None), [], [], new(0, 0, 0, 0, 0, 0));
}

