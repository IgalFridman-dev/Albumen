using PhotoSift.Domain.Action;
using PhotoSift.Infrastructure.Windows.Action;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.ActionExecution;

public sealed class FileOperationProgressSinkTests
{
    [Fact]
    public void FailedFinishOperationsCannotDisappearBehindSuccessfulDeleteCallback()
    {
        var sink = new FileOperationProgressSink(item => item == 11, () => ActionSafetyReason.None, CancellationToken.None);
        Assert.Equal(0, sink.PreDeleteItem(0, 11));
        Assert.Equal(0, sink.PostDeleteItem(0, 11, 0, 22));
        sink.FinishOperations(unchecked((int)0x80004005));
        Assert.Equal(unchecked((int)0x80004005), sink.Evidence.FinishOperationsHResult);
        var evidence = sink.Evidence with { PerformOperationsHResult = 0, OperationsAborted = false };
        Assert.Equal(PhotoSift.Application.Action.RecycleBinOutcomeKind.UnknownPendingRevalidation,
            RecycleBinClassifier.Classify(evidence, ActionSafetyReason.None).Outcome);
    }

    [Fact]
    public void SoleMatchingPreDeleteReleasesSynchronouslyBeforePermission()
    {
        var released = false;
        var sink = new FileOperationProgressSink(item => item == 11, () =>
        {
            released = true;
            return ActionSafetyReason.None;
        }, CancellationToken.None);
        Assert.False(sink.Evidence.PermissionGranted);
        Assert.Equal(0, sink.PreDeleteItem(0, 11));
        Assert.True(released);
        Assert.True(sink.Evidence.PermissionGranted);
        Assert.Equal(1, sink.Evidence.ValidPreDeleteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void UnexpectedItemPoisonsPermissionPermanently(int callbackItem)
    {
        var releases = 0;
        var sink = new FileOperationProgressSink(item => item == 11, () => { releases++; return ActionSafetyReason.None; }, CancellationToken.None);
        Assert.True(sink.PreDeleteItem(0, callbackItem) < 0);
        Assert.True(sink.PreDeleteItem(0, 11) < 0);
        Assert.False(sink.Evidence.PermissionGranted);
        Assert.Equal(0, releases);
        Assert.True(sink.Evidence.UnexpectedPreDeleteCount > 0);
    }

    [Fact]
    public void RepeatedValidPreDeleteNeverReleasesTwiceAndRecordsContradiction()
    {
        var releases = 0;
        var sink = new FileOperationProgressSink(item => item == 11, () => { releases++; return ActionSafetyReason.None; }, CancellationToken.None);
        Assert.Equal(0, sink.PreDeleteItem(0, 11));
        Assert.True(sink.PreDeleteItem(0, 11) < 0);
        Assert.Equal(1, releases);
        Assert.True(sink.Evidence.UnexpectedPreDeleteCount > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackExceptionsAreCapturedAndCannotEscape(bool identityThrows)
    {
        var sink = new FileOperationProgressSink(item => identityThrows ? throw new InvalidOperationException() : item == 11,
            () => throw new InvalidOperationException(), CancellationToken.None);
        Assert.True(sink.PreDeleteItem(0, 11) < 0);
        Assert.True(sink.Evidence.CallbackException);
        Assert.False(sink.Evidence.PermissionGranted);
    }

    [Fact]
    public void PostIdentityExceptionOverridesExistingSuccessfulPost()
    {
        var fail = false;
        var sink = new FileOperationProgressSink(item => fail ? throw new InvalidOperationException() : item == 11,
            () => ActionSafetyReason.None, CancellationToken.None);
        Assert.Equal(0, sink.PreDeleteItem(0, 11));
        Assert.Equal(0, sink.PostDeleteItem(0, 11, 0, 22));
        fail = true;
        Assert.True(sink.PostDeleteItem(0, 11, 0, 22) < 0);
        Assert.True(sink.Evidence.CallbackException);
        Assert.Equal(1, sink.Evidence.MatchingPostDeleteCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationOnlyDeniesBeforePermission(bool before)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new FileOperationProgressSink(item => item == 11, () => ActionSafetyReason.None, cancellation.Token);
        if (before) cancellation.Cancel();
        Assert.Equal(before, sink.PreDeleteItem(0, 11) < 0);
        cancellation.Cancel();
        if (!before)
        {
            Assert.Equal(0, sink.PostDeleteItem(0, 11, 0, 22));
            Assert.Equal(1, sink.Evidence.MatchingPostDeleteCount);
        }
        Assert.Equal(!before, sink.Evidence.PermissionGranted);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("mismatch")]
    [InlineData("beforePermission")]
    [InlineData("otherOperation")]
    public void InvalidCallbackSequenceIsRecorded(string sequence)
    {
        var sink = new FileOperationProgressSink(item => item == 11, () => ActionSafetyReason.None, CancellationToken.None);
        if (sequence != "beforePermission") Assert.Equal(0, sink.PreDeleteItem(0, 11));
        if (sequence == "otherOperation") Assert.True(sink.PreRenameItem(0, 11, 0) < 0);
        else
        {
            sink.PostDeleteItem(0, sequence == "mismatch" ? 12 : 11, 0, 22);
            if (sequence == "duplicate") sink.PostDeleteItem(0, 11, 0, 22);
        }
        Assert.True(sink.Evidence.UnexpectedPostDeleteCount > 0 || sink.Evidence.UnexpectedPreDeleteCount > 0);
    }
}
