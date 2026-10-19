using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Tests.Scan;
using Albumen.Domain.Action;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Review;

public sealed class ProtectedActionSafetyValidatorTests
{
    [Theory]
    [InlineData(1, "path")]
    [InlineData(3, "path")]
    [InlineData(1, "key")]
    [InlineData(3, "key")]
    [InlineData(1, "digest")]
    [InlineData(3, "digest")]
    [InlineData(1, "outside")]
    [InlineData(3, "outside")]
    [InlineData(1, "reparse")]
    [InlineData(3, "reparse")]
    [InlineData(1, "synchronized")]
    [InlineData(3, "synchronized")]
    [InlineData(1, "placeholder")]
    [InlineData(3, "placeholder")]
    public async Task SelectedAndUnselectedCurrentFailuresCannotBecomeSafeObservations(int failedId, string change)
    {
        var context = ScanTestData.Context();
        var analyzer = new ScanTestAnalyzer
        {
            Hash = (content, _) => ValueTask.FromResult(new StreamDigest(1,
                content.Length == 2 ? new string('B', 64) : ScanTestData.Digest)),
        };
        var reader = new ScanTestReader { Create = (_, request) =>
        {
            var lease = new ScanTestLease(request);
            if (request.ExpectedPhysicalFileKey.FileId.Low != (ulong)failedId) return lease;
            if (change is "reparse" or "synchronized" or "placeholder")
                throw new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation), change);
            if (change == "digest") lease.Content = new MemoryStream([1, 2]);
            lease.Refresh = _ => ValueTask.FromResult(change switch
            {
                "key" => lease.OpenedFacts with { PhysicalFileKey = new(99, new(0, 99)) },
                "path" => lease.OpenedFacts with { ResolvedPath = @"C:\Photos\replaced.jpg" },
                "outside" => lease.OpenedFacts with { ResolvedPath = @"C:\Elsewhere\1.jpg" },
                _ => lease.OpenedFacts,
            });
            return lease;
        } };
        var result = await new ProtectedActionSafetyValidator(reader, analyzer).ProbeAsync(context,
            ReviewData.Groups(context, 3), [], (groups, reason) =>
            {
                Assert.Equal([(long)failedId], groups.Single().MissingOccurrenceIds);
                return Task.FromResult(reason);
            }, TestContext.Current.CancellationToken);
        Assert.Equal(change is "outside" or "reparse" or "synchronized" or "placeholder"
            ? ActionSafetyReason.UnsafeLocation : ActionSafetyReason.Changed, result);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("stale")]
    [InlineData("faulted")]
    public async Task AllLeasesSurviveEveryAdmittedAcknowledgmentIncludingCancellation(string result)
    {
        var context = ScanTestData.Context();
        var groups = ReviewData.Groups(context, 3);
        var reader = new ScanTestReader();
        var entered = ScanTestData.Signal<bool>();
        var receipt = ScanTestData.Signal<string>();
        using var canceled = new CancellationTokenSource();
        var probe = new ProtectedActionSafetyValidator(reader, new ScanTestAnalyzer()).ProbeAsync(
            context, groups, [], async (observations, reason) =>
            {
                Assert.Equal(ActionSafetyReason.None, reason);
                Assert.Equal(3, observations.Single().Observations.Length);
                entered.TrySetResult(true);
                return await receipt.Task;
            }, canceled.Token);
        await ScanTestData.Bound(entered.Task);
        canceled.Cancel();
        Assert.Equal(3, reader.Live);
        Assert.False(probe.IsCompleted);
        if (result == "faulted") receipt.TrySetException(new InvalidOperationException("acknowledgment"));
        else receipt.TrySetResult(result);
        if (result == "faulted") await Assert.ThrowsAsync<InvalidOperationException>(() => probe);
        else Assert.Equal(result, await probe);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(ScanReason.Missing, ActionSafetyReason.Missing)]
    [InlineData(ScanReason.Changed, ActionSafetyReason.Changed)]
    [InlineData(ScanReason.AccessDenied, ActionSafetyReason.AccessDenied)]
    [InlineData(ScanReason.SharingViolation, ActionSafetyReason.SharingViolation)]
    [InlineData(ScanReason.UnsafeLocation, ActionSafetyReason.UnsafeLocation)]
    [InlineData(ScanReason.UnknownSafety, ActionSafetyReason.UnknownSafety)]
    public async Task ProtectedReaderUnsafeFailuresAreAcknowledgedBeforeLeaseCleanup(ScanReason readReason, ActionSafetyReason expected)
    {
        var context = ScanTestData.Context();
        var reader = new ScanTestReader { Create = (_, request) => new ScanTestLease(request)
        {
            Refresh = _ => throw new ScanReadException(new(
                readReason is ScanReason.UnsafeLocation or ScanReason.UnknownSafety
                    ? ScanStageState.FailedTerminal : ScanStageState.FailedRetryable, readReason), "unsafe"),
        } };
        var reason = await new ProtectedActionSafetyValidator(reader, new ScanTestAnalyzer()).ProbeAsync(
            context, ReviewData.Groups(context, 2), [], (_, failure) =>
            {
                Assert.True(reader.Live > 0);
                return Task.FromResult(failure);
            }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, reason);
        Assert.Equal(0, reader.Live);
    }
}
