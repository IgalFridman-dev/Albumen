using System.Collections.Immutable;
using Albumen.Application.Review;
using Albumen.Domain.Action;

namespace Albumen.Application.Action;

// Mutated exclusively by ActionCoordinator's data turns. Snapshots and every
// row are immutable, including snapshots already delivered to observers.
internal sealed class ActionStore
{
    internal ActionStore(ApprovedActionRequest request)
    {
        Current = new(request.RequestId, ActionStatus.Claiming,
            request.Items.Select(item => new ActionItemSnapshot(item, null, ActionItemState.Waiting,
                ActionSafetyReason.None)).ToImmutableArray(), 0, 0, 0, 0, 0, ActionSafetyReason.None);
    }
    internal ActionSnapshot Current { get; private set; }
    internal void Running()
    {
        if (Current.Status != ActionStatus.Faulted) Current = Current with { Status = ActionStatus.Running };
    }
    internal void Reject(int index, ActionSafetyReason reason) =>
        Change(index, ActionItemState.Waiting, ActionItemState.NotRequested, reason);
    internal void Requested(int index, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An Action item identity is required.", nameof(id));
        Change(index, ActionItemState.Waiting, ActionItemState.Requested, ActionSafetyReason.None, id);
    }
    internal void Interrupt(int index, ActionSafetyReason reason) =>
        Change(index, ActionItemState.Requested, ActionItemState.InterruptedBeforeExecution, reason);
    internal void Executing(int index) =>
        Change(index, ActionItemState.Requested, ActionItemState.Executing, ActionSafetyReason.None);
    internal void Uncertain(int index, ActionSafetyReason reason) =>
        Change(index, ActionItemState.Executing, ActionItemState.UnknownPendingRevalidation, reason);
    internal void Terminal(int index, ActionTerminalKind kind, ActionSafetyReason reason) =>
        Change(index, ActionItemState.Executing, kind switch
        {
            ActionTerminalKind.Recycled => ActionItemState.Recycled,
            ActionTerminalKind.NotRemoved => ActionItemState.NotRemoved,
            ActionTerminalKind.UnknownPendingRevalidation => ActionItemState.UnknownPendingRevalidation,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, reason);
    internal void Fault(ActionSafetyReason reason) => Current = Current with
    {
        Status = ActionStatus.Faulted,
        FaultReason = Current.FaultReason == ActionSafetyReason.None ? reason : Current.FaultReason,
    };
    internal void Finish(ActionSafetyReason reason)
    {
        for (var index = 0; index < Current.Items.Length; index++)
        {
            switch (Current.Items[index].State)
            {
                case ActionItemState.Waiting: Reject(index, reason); break;
                case ActionItemState.Requested: Interrupt(index, reason); break;
                case ActionItemState.Executing: Uncertain(index, reason); break;
            }
        }
        if (Current.Status != ActionStatus.Faulted)
            Current = Current with { Status = Current.Items.All(row => row.State is ActionItemState.Recycled or ActionItemState.NotRemoved)
                ? ActionStatus.Complete : ActionStatus.Incomplete };
    }
    internal ImmutableArray<PriorActionOutcome> Preceding(int index) => Current.Items.Take(index)
        .Select(row => new PriorActionOutcome(row.Item.GroupId, row.Item.Occurrence.OccurrenceId, row.State switch
        {
            ActionItemState.Recycled => PriorActionDisposition.Recycled,
            ActionItemState.NotRemoved => PriorActionDisposition.AffirmedNotRemoved,
            ActionItemState.NotRequested => PriorActionDisposition.NotRequested,
            ActionItemState.Waiting => PriorActionDisposition.Pending,
            _ => PriorActionDisposition.Unknown,
        })).ToImmutableArray();
    private void Change(int index, ActionItemState predecessor, ActionItemState state,
        ActionSafetyReason reason, Guid? id = null)
    {
        var row = Current.Items[index];
        if (row.State != predecessor) throw new InvalidOperationException("Invalid Action predecessor.");
        var rows = Current.Items.SetItem(index, row with { ActionItemId = id ?? row.ActionItemId, State = state, Reason = reason });
        Current = Current with
        {
            Items = rows,
            DurableRequestedCount = rows.LongCount(item => item.ActionItemId is not null),
            DurableExecutingCount = rows.LongCount(item => item.State is ActionItemState.Executing or ActionItemState.Recycled
                or ActionItemState.NotRemoved or ActionItemState.UnknownPendingRevalidation),
            RecycledCount = rows.LongCount(item => item.State == ActionItemState.Recycled),
            NotRemovedCount = rows.LongCount(item => item.State == ActionItemState.NotRemoved),
            UnknownCount = rows.LongCount(item => item.State == ActionItemState.UnknownPendingRevalidation),
        };
    }
}
