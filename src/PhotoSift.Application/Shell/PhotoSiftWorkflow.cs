using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

// Navigation and rule confirmation only. Scope and Scan remain owned by their
// existing workflows; every observer/command transition enters a non-inline inbox.
public sealed class PhotoSiftWorkflow : IPhotoSiftWorkflow
{
    private readonly IChooseScopeWorkflow scope;
    private readonly IScanCoordinator scan;
    private readonly WorkflowInbox inbox;
    private PhotoSiftSnapshot current;
    private long nextRuleRevision;
    private ScanSnapshot? pendingScan;
    private int scanRefreshScheduled;

    public PhotoSiftWorkflow(IChooseScopeWorkflow scope, IScanCoordinator scan, IWorkflowScheduler scheduler)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.scan = scan ?? throw new ArgumentNullException(nameof(scan));
        inbox = new(scheduler ?? throw new ArgumentNullException(nameof(scheduler)));
        current = new(scope.Current, null, EmptyScan(), WorkflowPhase.ChooseScope, InitialWorkflow.Create());
        scope.SnapshotChanged += OnScopeChanged;
        scan.SnapshotChanged += OnScanChanged;
    }

    public PhotoSiftSnapshot Current => Volatile.Read(ref current);
    public event EventHandler<PhotoSiftSnapshot>? SnapshotChanged;

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
        await inbox.InvokeAsync(RefreshScope).ConfigureAwait(false);
        await invalidation.ConfigureAwait(false);
    }

    private void RefreshScope()
    {
        var accepted = scope.Current;
        if (accepted.Revision != current.Scope.Revision)
        {
            Publish(current with { Scope = accepted, Rules = null, Scan = EmptyScan(), CurrentPhase = WorkflowPhase.ChooseScope });
        }
        else if (!ReferenceEquals(accepted, current.Scope))
        {
            Publish(current with { Scope = accepted });
        }
    }

    private void OnScanChanged(object? sender, ScanSnapshot snapshot)
    {
        // Coalesce presentation notifications, not accepted results: the latest
        // immutable snapshot retains all terminal rows and evidence.
        Interlocked.Exchange(ref pendingScan, snapshot);
        ScheduleScanRefresh();
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

