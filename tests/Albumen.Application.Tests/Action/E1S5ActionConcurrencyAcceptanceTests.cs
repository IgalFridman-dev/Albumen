using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Application.Tests.Review;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Action;
using Xunit;

namespace Albumen.Application.Tests.ActionExecution;

public sealed class E1S5ActionConcurrencyAcceptanceTests
{
    [Fact]
    public async Task SealedReviewAuthorityAndRequestBoundFaultDrainTerminalDespiteCancellationAndRepeatedApproval()
    {
        var reader = ApprovedActionAuthorityTests.Reader();
        await using var review = new ApprovalFixture(new(), actionSafety: new(reader, new ScanTestAnalyzer()));
        var request = await ApprovedActionAuthorityTests.ApproveAsync(review);
        await using var ports = new ActionFixture(2) { GateAt = "terminal:1" };
        var owner = new ActionCoordinator(review.Scheduler, review.Owner, ports, ports, ports);
        using var cancellation = new CancellationTokenSource();
        var pending = owner.ApproveAsync(request, cancellation.Token);
        try
        {
            await review.PumpAsync(ports.Entered.Task);
            Assert.Same(request, review.Owner.Current.ApprovedRequest);
            Assert.Same(request, Assert.Single(ports.RequestedFacts).Safety.Claim.Request);
            Assert.Equal(ActionItemState.Executing, owner.Current.Items[0].State);
            Assert.False(ports.GatedToken.CanBeCanceled);
            Assert.False(pending.IsCompleted);

            cancellation.Cancel();
            var staleFault = owner.ReportObserverFaultAsync(Guid.NewGuid());
            await review.PumpAsync(staleFault);
            Assert.Equal(ActionStatus.Running, owner.Current.Status);
            var repeated = owner.ApproveAsync(request, CancellationToken.None);
            await review.PumpAsync(repeated);
            Assert.Equal(ActionCommandOutcome.Existing, (await repeated).Outcome);
            await review.PumpAsync(owner.ReportObserverFaultAsync(request.RequestId));
            Assert.Equal(ActionStatus.Faulted, owner.Current.Status);
            Assert.False(pending.IsCompleted);
            ports.Release.TrySetResult(true);
            await review.PumpAsync(pending);

            var result = await pending;
            Assert.Equal(ActionCommandOutcome.Faulted, result.Outcome);
            Assert.Equal(ActionSafetyReason.ProgrammingFault, result.Snapshot.FaultReason);
            Assert.Equal([ActionItemState.Recycled, ActionItemState.NotRequested], result.Snapshot.Items.Select(item => item.State));
            Assert.Equal(["requested:1", "executing:1", "execute:1", "terminal:1"], ports.Calls);
            Assert.Single(ports.TerminalFacts);
            Assert.Equal(1L, result.Snapshot.DurableRequestedCount);
            Assert.Equal(1L, result.Snapshot.DurableExecutingCount);
            Assert.True(review.Owner.Current.Sealed);
            Assert.Equal(request.SelectedOccurrenceIds, review.Owner.Current.ApprovedRequest!.SelectedOccurrenceIds);
            Assert.Equal(0, reader.Live);
        }
        finally
        {
            ports.Release.TrySetResult(true);
            await review.PumpAsync(pending);
            await review.PumpAsync(owner.DisposeAsync().AsTask());
        }
    }
}
