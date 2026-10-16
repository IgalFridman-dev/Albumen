using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;
using Xunit;

namespace PhotoSift.Application.Tests.ActionExecution;

public sealed class ActionStoreTests
{
    [Fact]
    public void SnapshotsRetainRowTruthAndDurableCountsAcrossFaultAndInterruption()
    {
        var store = new ActionStore(ActionFixture.RequestFor(3));
        var initial = store.Current;
        store.Requested(0, Guid.NewGuid());
        var requested = store.Current;
        store.Executing(0);
        store.Terminal(0, ActionTerminalKind.Recycled, ActionSafetyReason.None);
        store.Requested(1, Guid.NewGuid());
        store.Interrupt(1, ActionSafetyReason.Interrupted);
        store.Reject(2, ActionSafetyReason.Interrupted);
        store.Fault(ActionSafetyReason.ProgrammingFault);
        Assert.All(initial.Items, row => Assert.Equal(ActionItemState.Waiting, row.State));
        Assert.Equal(ActionItemState.Requested, requested.Items[0].State);
        Assert.Equal(2, store.Current.DurableRequestedCount);
        Assert.Equal(1, store.Current.DurableExecutingCount);
        Assert.Equal(1, store.Current.RecycledCount);
        Assert.Equal(0, store.Current.NotRemovedCount);
        Assert.Equal(0, store.Current.UnknownCount);
        Assert.Equal([ActionItemState.Recycled, ActionItemState.InterruptedBeforeExecution,
            ActionItemState.NotRequested], store.Current.Items.Select(row => row.State));
        var forged = store.Current with { Items = [] };
        Assert.Empty(forged.Items);
        Assert.Equal(3, store.Current.Items.Length);
    }

    [Theory]
    [InlineData(ActionTerminalKind.Recycled, ActionItemState.Recycled, 1, 0, 0)]
    [InlineData(ActionTerminalKind.NotRemoved, ActionItemState.NotRemoved, 0, 1, 0)]
    [InlineData(ActionTerminalKind.UnknownPendingRevalidation, ActionItemState.UnknownPendingRevalidation, 0, 0, 1)]
    public void TerminalsRequireExactPredecessorAndCannotBeOverwritten(ActionTerminalKind kind,
        ActionItemState state, long recycled, long notRemoved, long unknown)
    {
        var store = new ActionStore(ActionFixture.RequestFor(1));
        Assert.Throws<InvalidOperationException>(() => store.Executing(0));
        Assert.Throws<InvalidOperationException>(() => store.Terminal(0, kind, ActionSafetyReason.None));
        store.Requested(0, Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => store.Requested(0, Guid.NewGuid()));
        store.Executing(0);
        store.Terminal(0, kind, ActionSafetyReason.None);
        Assert.Throws<InvalidOperationException>(() => store.Reject(0, ActionSafetyReason.Missing));
        Assert.Throws<InvalidOperationException>(() => store.Terminal(0, kind, ActionSafetyReason.None));
        Assert.Equal(state, store.Current.Items[0].State);
        Assert.Equal((1L, 1L, recycled, notRemoved, unknown), (store.Current.DurableRequestedCount,
            store.Current.DurableExecutingCount, store.Current.RecycledCount, store.Current.NotRemovedCount, store.Current.UnknownCount));
    }
}
