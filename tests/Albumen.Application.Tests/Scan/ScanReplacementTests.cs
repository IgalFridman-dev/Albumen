using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ScanReplacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullInvalidationRevokesOldConfirmationWhileIdleUntilNewerRulesAreConfirmed(bool startFirst)
    {
        await using var fixture = new CoordinatorFixture();
        if (startFirst)
        {
            await fixture.Start(7, 9);
            await fixture.Run();
        }
        else await fixture.Pump(fixture.Owner.InvalidateAsync(7, 9));

        await fixture.Pump(fixture.Owner.InvalidateAsync(7, null));
        Assert.Equal(ScanStatus.Idle, fixture.Owner.Current.Status);
        // Repeated revocation while Idle and delayed confirmation delivery must
        // neither forget revocation nor consume the next legitimate revision.
        await fixture.Pump(fixture.Owner.InvalidateAsync(7, null));
        await fixture.Pump(fixture.Owner.InvalidateAsync(7, 9));
        await fixture.Start(7, 9);
        Assert.Equal(ScanStatus.Idle, fixture.Owner.Current.Status);
        Assert.Null(fixture.Owner.Current.Pin);
        Assert.Equal(startFirst ? 1 : 0, fixture.Executor.Started.Count);

        fixture.Executor.Arrived = ScanTestData.Signal<CoordinatorRun>();
        await fixture.Pump(fixture.Owner.InvalidateAsync(7, 10));
        await fixture.Start(7, 10);
        var fresh = await fixture.Run();
        Assert.Equal(10, fresh.Context.Pin.RuleSetRevision);
        Assert.Equal(ScanStatus.Running, fixture.Owner.Current.Status);
    }

    [Fact]
    public async Task ReplacementIsAcceptedBeforePredecessorCancellationReentry()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var old = await fixture.Run();
        var entered = ScanTestData.Signal<ScanPin>();
        old.OnCancel = async () =>
        {
            entered.TrySetResult(fixture.Owner.Current.Pin!);
            await fixture.Owner.InvalidateAsync(3, null);
        };
        await fixture.Start(2, 2);
        await fixture.Pump(entered.Task);
        Assert.Equal(2, (await entered.Task).ScopeRevision);
        Assert.NotEqual(old.Context.Pin.ScanId, (await entered.Task).ScanId);
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Idle);
    }

    [Fact]
    public async Task CanceledPriorPreflightCannotStartAndStaleCleanupCannotOverwriteReplacement()
    {
        await using var fixture = new CoordinatorFixture();
        var oldPreflight = ScanTestData.Signal<ScopePreflightEvidence>();
        var entered = ScanTestData.Signal<bool>();
        fixture.Preflight.Inspect = (_, _) => { entered.TrySetResult(true); return oldPreflight.Task; };
        await fixture.Start();
        await fixture.Pump(entered.Task);
        fixture.Preflight.Inspect = (_, _) => Task.FromResult(fixture.Evidence);
        await fixture.Start(2, 2);
        var replacement = await fixture.Run();
        oldPreflight.SetResult(fixture.Evidence);
        await fixture.Pump(fixture.Owner.InvalidateAsync(1, 1));
        Assert.Equal(replacement.Context.Pin, fixture.Owner.Current.Pin);
        Assert.Single(fixture.Executor.Started);
    }

    [Fact]
    public async Task EligibilityWatermarkRejectsDelayedStartEvenWhileIdle()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Pump(fixture.Owner.InvalidateAsync(7, 9));
        await fixture.Start(6, 8);
        Assert.Equal(ScanStatus.Idle, fixture.Owner.Current.Status);
        Assert.Null(fixture.Owner.Current.Pin);
        Assert.Empty(fixture.Executor.Started);
        await fixture.Start(7, 8);
        Assert.Equal(ScanStatus.Idle, fixture.Owner.Current.Status);
        await fixture.Start(7, 9);
        await fixture.Run();
        Assert.Equal(7, fixture.Owner.Current.Pin!.ScopeRevision);
    }

    [Fact]
    public async Task RetryAllocatesFreshPinAndRecomputesSignatures()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var old = await fixture.Run();
        var registration = fixture.Owner.RegisterAsync(old.Context, ScanTestData.Work(1, old.Context).File).AsTask();
        await fixture.Pump(registration);
        var work = Assert.IsType<OccurrenceWork>(await registration);
        await fixture.Pump(fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(work)).AsTask());
        fixture.Executor.Arrived = ScanTestData.Signal<CoordinatorRun>();
        await fixture.Start();
        var fresh = await fixture.Run();
        Assert.NotEqual(old.Context.Pin.ScanId, fresh.Context.Pin.ScanId);
        Assert.Empty(fixture.Owner.Current.Occurrences);
        var stale = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(work)).AsTask();
        await fixture.Pump(stale);
        Assert.False((await stale).Accepted);
        var next = fixture.Owner.RegisterAsync(fresh.Context, ScanTestData.Work(1, fresh.Context).File).AsTask();
        await fixture.Pump(next);
        Assert.NotNull(await next);
        Assert.Equal(ScanStageState.Running, fixture.Owner.Current.Occurrences.Single().Exact.State);
    }
}
