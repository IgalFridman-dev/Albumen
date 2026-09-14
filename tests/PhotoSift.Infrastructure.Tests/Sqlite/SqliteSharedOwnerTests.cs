using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteSharedOwnerTests
{
    [Fact]
    public async Task ActionAndOverrideWritesShareOneFifoLaneAndDisposalDrainsBothAdmittedPorts()
    {
        await using var fixture = new SqliteOverrideFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;
        var observer = new SqliteDatabaseObserver(LaneWaitStarted: completed => { if (!completed) queued.TrySetResult(); },
            LaneEntered: async _ => { if (Interlocked.Increment(ref entries) == 1) { entered.TrySetResult(); await release.Task; } });
        var stores = new SqlitePhotoSiftStores(fixture.DatabasePath, observer);
        try
        {
            var first = stores.OverrideEvidence.PersistAsync(OverrideEvidenceData.Create(), TestContext.Current.CancellationToken).AsTask();
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = stores.ActionJournal.PersistRequestedAsync(JournalData.Requested(), TestContext.Current.CancellationToken).AsTask();
            await queued.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposal = stores.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, entries);
            release.TrySetResult();
            Assert.Equal(OverridePersistOutcome.Persisted, (await first).Outcome);
            Assert.Equal(ActionJournalWriteOutcome.Persisted, (await second).Outcome);
            await disposal;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(JournalData.Requested()), TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => stores.OverrideEvidence.ReadAsync(OverrideEvidenceData.KnownEvidenceId, TestContext.Current.CancellationToken).AsTask());
        }
        finally { release.TrySetResult(); await stores.DisposeAsync(); }
    }

    [Fact]
    public async Task ProviderWorkRunsOutsideCallingSynchronizationContextEvenWithAnEmptyLane()
    {
        await using var fixture = new SqliteOverrideFixture();
        var marker = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        var observed = false;
        var observer = new SqliteDatabaseObserver(BeforeActionTransaction: (_, _) =>
        {
            Assert.NotSame(marker, SynchronizationContext.Current);
            observed = true;
            return ValueTask.CompletedTask;
        });
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath, observer);
        Task<ActionJournalWriteResult> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(marker);
            operation = stores.ActionJournal.PersistRequestedAsync(JournalData.Requested(), TestContext.Current.CancellationToken).AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await operation).Outcome);
        Assert.True(observed);
    }
}
