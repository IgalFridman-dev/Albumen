using System.Collections.Immutable;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Action;

namespace PhotoSift.Application.Action;

public interface IActionRecoveryCoordinator : IAsyncDisposable
{
    ActionRecoverySnapshot Current { get; }
    event EventHandler<ActionRecoverySnapshot>? SnapshotChanged;
    Task LoadAsync(CancellationToken cancellationToken);
    Task RevalidateUnknownAsync(Guid requestId, CancellationToken cancellationToken);
}

public enum ActionRecoveryStatus { NotLoaded, Loading, Ready, Faulted }

public sealed record ActionRecordItemSnapshot(ActionRequestedFact Requested, ActionRecordEvidenceState State,
    ActionSafetyReason ExecutionReason, ActionRevalidationReason? RevalidationReason);

public sealed record ActionRecordSnapshot(Guid RequestId, DateTimeOffset RequestedUtc,
    ImmutableArray<ActionRecordItemSnapshot> Items, ActionRecordSummary Summary);

public sealed record ActionRecoverySnapshot(ActionRecoveryStatus Status,
    ImmutableArray<ActionRecordSnapshot> Records, Guid? ActiveActionItemId, bool ProbeOutstanding)
{
    public static ActionRecoverySnapshot NotLoaded { get; } = new(ActionRecoveryStatus.NotLoaded, [], null, false);
}

public sealed class ActionRecoveryCoordinator : IActionRecoveryCoordinator
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);
    private readonly ActionOwnerInbox inbox;
    private readonly IActionRecordStore store;
    private readonly IActionRevalidationProbe probe;
    private readonly Func<TimeSpan, CancellationToken, Task> waitForDeadline;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly TimeSpan deadline;
    private readonly Queue<ActionRecoverySnapshot> publications = [];
    private ImmutableArray<PersistedActionItem> items = [];
    private ActionRecoverySnapshot current = ActionRecoverySnapshot.NotLoaded;
    private Attempt? outstanding;
    private long generation;
    private bool disposed;

    public ActionRecoveryCoordinator(IWorkflowScheduler scheduler, IActionRecordStore store,
        IActionRevalidationProbe probe, int resultCapacity = 16)
        : this(scheduler, store, probe,
            (duration, token) => Task.Delay(duration, token), () => DateTimeOffset.UtcNow,
            DefaultDeadline, resultCapacity)
    {
    }

    internal ActionRecoveryCoordinator(IWorkflowScheduler scheduler, IActionRecordStore store,
        IActionRevalidationProbe probe, Func<TimeSpan, CancellationToken, Task> waitForDeadline,
        Func<DateTimeOffset> utcNow, TimeSpan deadline, int resultCapacity = 16)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.waitForDeadline = waitForDeadline ?? throw new ArgumentNullException(nameof(waitForDeadline));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        this.deadline = deadline;
        inbox = new(scheduler, resultCapacity) { DeliverAfterTurn = DeliverPublications };
    }

    public ActionRecoverySnapshot Current => Volatile.Read(ref current);
    public event EventHandler<ActionRecoverySnapshot>? SnapshotChanged;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var admitted = await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || current.Status is ActionRecoveryStatus.Loading or ActionRecoveryStatus.Ready) return false;
            Publish(current with { Status = ActionRecoveryStatus.Loading, Records = [] });
            return true;
        }).ConfigureAwait(false);
        if (!admitted) return;

        ActionRecordLoadResult result;
        try { result = await store.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new(ActionRecordLoadOutcome.Unavailable, []);
        }
        catch (Exception) { result = new(ActionRecordLoadOutcome.Unavailable, []); }

        await inbox.CommandAsync(() =>
        {
            if (disposed) return false;
            if (result.Outcome != ActionRecordLoadOutcome.Loaded)
            {
                items = [];
                Publish(new(ActionRecoveryStatus.Faulted, [], null, false));
                return false;
            }
            items = result.Items.IsDefault ? [] : result.Items;
            Publish(new(ActionRecoveryStatus.Ready, Project(items), null, false));
            return true;
        }).ConfigureAwait(false);
    }

    public async Task RevalidateUnknownAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty) return;
        var selected = await inbox.CommandAsync(() =>
        {
            if (disposed || current.Status != ActionRecoveryStatus.Ready || outstanding is not null
                || cancellationToken.IsCancellationRequested) return ImmutableArray<Guid>.Empty;
            return items.Where(item => item.RequestId == requestId && NeedsRevalidation(item))
                .Select(item => item.Requested.ActionItemId).ToImmutableArray();
        }).ConfigureAwait(false);

        foreach (var actionItemId in selected)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var attempt = await inbox.CommandAsync(() => Admit(actionItemId, cancellationToken)).ConfigureAwait(false);
            if (attempt is null) return;
            var canContinue = await RunAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
            if (!canContinue) return;
        }
    }

    public ValueTask DisposeAsync() => new(inbox.CommandAsync(() =>
    {
        if (disposed) return true;
        disposed = true;
        generation++;
        publications.Clear();
        return true;
    }));

    private Attempt? Admit(Guid actionItemId, CancellationToken cancellationToken)
    {
        if (disposed || outstanding is not null || cancellationToken.IsCancellationRequested) return null;
        var item = items.SingleOrDefault(value => value.Requested.ActionItemId == actionItemId);
        if (item is null || !NeedsRevalidation(item)) return null;
        var attempt = new Attempt(checked(++generation), item);
        outstanding = attempt;
        Publish(current with { ActiveActionItemId = actionItemId, ProbeOutstanding = true });
        return attempt;
    }

    private async Task<bool> RunAttemptAsync(Attempt attempt, CancellationToken caller)
    {
        var request = Request(attempt.Item);
        var probeTask = Task.Run(async () => await probe.ProbeAsync(request, CancellationToken.None).ConfigureAwait(false),
            CancellationToken.None);
        attempt.ProbeTask = probeTask;
        var deadlineTask = waitForDeadline(deadline, CancellationToken.None);
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = caller.Register(() => cancellation.TrySetResult());
        var winner = await Task.WhenAny(probeTask, deadlineTask, cancellation.Task).ConfigureAwait(false);

        if (winner == probeTask)
        {
            ActionRevalidationProbeResult result;
            try { result = await probeTask.ConfigureAwait(false); }
            catch (Exception)
            {
                result = Unknown(ActionRevalidationReason.Failed);
            }
            var accepted = await inbox.CommandAsync<ActionRevalidationProbeResult?>(() =>
            {
                if (disposed || !ReferenceEquals(outstanding, attempt)
                    || attempt.Expired || attempt.Generation != generation) return null;
                return caller.IsCancellationRequested ? Unknown(ActionRevalidationReason.Failed) : result;
            }).ConfigureAwait(false);
            if (accepted is null)
                return false;
            return await CompleteAsync(attempt, accepted, probeSettled: true).ConfigureAwait(false);
        }

        var reason = winner == deadlineTask ? ActionRevalidationReason.TimedOut : ActionRevalidationReason.Failed;
        await ExpireAsync(attempt, reason).ConfigureAwait(false);
        _ = SettleLateAsync(attempt, probeTask);
        return false;
    }

    private async Task ExpireAsync(Attempt attempt, ActionRevalidationReason reason)
    {
        var accepted = await inbox.CommandAsync(() =>
        {
            if (disposed || !ReferenceEquals(outstanding, attempt)) return false;
            attempt.Expired = true;
            generation++;
            return true;
        }).ConfigureAwait(false);
        if (accepted)
            await PersistAsync(attempt, Unknown(reason), probeSettled: false).ConfigureAwait(false);
    }

    private Task<bool> CompleteAsync(Attempt attempt, ActionRevalidationProbeResult result, bool probeSettled) =>
        PersistAsync(attempt, result, probeSettled);

    private async Task<bool> PersistAsync(Attempt attempt, ActionRevalidationProbeResult result, bool probeSettled)
    {
        var fact = new ActionRevalidationFact(Guid.NewGuid(), attempt.Item.Requested.ActionItemId, utcNow(),
            result.Kind, result.Reason, result.Observation);
        ActionRevalidationWriteResult written;
        try { written = await store.AppendRevalidationAsync(fact, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { written = new(ActionRevalidationWriteOutcome.Unavailable); }

        return await inbox.CommandAsync(() =>
        {
            if (disposed || !ReferenceEquals(outstanding, attempt)) return false;
            var accepted = written.Outcome is ActionRevalidationWriteOutcome.Persisted
                or ActionRevalidationWriteOutcome.ExactRetry;
            if (accepted)
            {
                var index = -1;
                for (var candidate = 0; candidate < items.Length; candidate++)
                    if (items[candidate].Requested.ActionItemId == fact.ActionItemId)
                    {
                        index = candidate;
                        break;
                    }
                if (index >= 0)
                {
                    var item = items[index];
                    items = items.SetItem(index, item with { Revalidations = item.Revalidations.Add(fact) });
                }
            }
            if (probeSettled)
            {
                outstanding = null;
                Publish(new(ActionRecoveryStatus.Ready, Project(items), null, false));
                return accepted;
            }
            Publish(new(ActionRecoveryStatus.Ready, Project(items), attempt.Item.Requested.ActionItemId, true));
            return false;
        }).ConfigureAwait(false);
    }

    private async Task SettleLateAsync(Attempt attempt, Task<ActionRevalidationProbeResult> task)
    {
        try { _ = await task.ConfigureAwait(false); }
        catch (Exception) { }
        await inbox.CommandAsync(() =>
        {
            if (disposed || !ReferenceEquals(outstanding, attempt)) return false;
            outstanding = null;
            Publish(new(ActionRecoveryStatus.Ready, Project(items), null, false));
            return true;
        }).ConfigureAwait(false);
    }

    private static ActionRevalidationRequest Request(PersistedActionItem item)
    {
        var reviewed = item.Requested.Safety.Item;
        return new(item.Requested.ActionItemId,
            reviewed.ResolvedPath, reviewed.PhysicalFileKey, reviewed.Content.ByteLength, reviewed.Content.Sha256);
    }

    private static ActionRevalidationProbeResult Unknown(ActionRevalidationReason reason) =>
        new(ActionRevalidationKind.StillUnknown, reason, new(false, null, null, null));

    private static ImmutableArray<ActionRecordSnapshot> Project(ImmutableArray<PersistedActionItem> source) =>
        source.GroupBy(item => item.RequestId).Select(group =>
        {
            var rows = group.Select(item => new ActionRecordItemSnapshot(item.Requested, State(item),
                item.Terminal?.Reason ?? ActionSafetyReason.None, item.Revalidations.LastOrDefault()?.Reason)).ToImmutableArray();
            var evidence = group.Select(item => new ActionRecordEvidence(item.Requested.Safety.Item.PhysicalFileKey,
                item.Requested.Safety.Item.Allocation.AllocatedBytes, State(item))).ToImmutableArray();
            return new ActionRecordSnapshot(group.Key, group.Min(item => item.Requested.RequestedUtc), rows,
                ActionRecordPolicy.Derive(evidence));
        }).OrderByDescending(record => record.RequestedUtc).ThenBy(record => record.RequestId).ToImmutableArray();

    private static ActionRecordEvidenceState State(PersistedActionItem item)
    {
        if (item.Terminal?.Kind == ActionTerminalKind.Recycled) return ActionRecordEvidenceState.Recycled;
        if (item.Terminal?.Kind == ActionTerminalKind.NotRemoved) return ActionRecordEvidenceState.NotRemoved;
        if (item.Revalidations.Any(fact => fact.Kind == ActionRevalidationKind.ConfirmedPresent))
            return ActionRecordEvidenceState.ConfirmedPresent;
        if (item.Terminal?.Kind == ActionTerminalKind.UnknownPendingRevalidation) return ActionRecordEvidenceState.Unknown;
        return item.Executing is null ? ActionRecordEvidenceState.Requested : ActionRecordEvidenceState.Executing;
    }

    private static bool NeedsRevalidation(PersistedActionItem item) => State(item) is
        ActionRecordEvidenceState.Requested or ActionRecordEvidenceState.Executing or ActionRecordEvidenceState.Unknown;

    private void Publish(ActionRecoverySnapshot snapshot)
    {
        if (disposed) return;
        Volatile.Write(ref current, snapshot);
        publications.Enqueue(snapshot);
    }

    private void DeliverPublications()
    {
        while (publications.TryDequeue(out var snapshot))
            foreach (var observer in SnapshotChanged?.GetInvocationList() ?? [])
                try { ((EventHandler<ActionRecoverySnapshot>)observer)(this, snapshot); }
                catch (Exception) { }
    }

    private sealed class Attempt(long generation, PersistedActionItem item)
    {
        internal long Generation { get; } = generation;
        internal PersistedActionItem Item { get; } = item;
        internal Task<ActionRevalidationProbeResult>? ProbeTask { get; set; }
        internal bool Expired { get; set; }
    }
}
