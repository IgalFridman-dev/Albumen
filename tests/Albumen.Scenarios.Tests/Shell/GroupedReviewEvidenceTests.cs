using Albumen.App.Shell;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class GroupedReviewEvidenceTests
{
    [Fact]
    public async Task ASelectionRefreshKeepsThePendingPreviewOfTheSamePhoto()
    {
        var source = new ControllableReviewPreviewSource();
        var scope = new ReviewStaticScope();
        var workflow = new RecordingReviewWorkflow(
            scope.Current, ReviewData.CreateSnapshot());
        await using var viewModel = new ShellViewModel(
            scope, workflow, reviewPreviewSource: source);
        var group = viewModel.ReviewCategories[0].Groups[0];
        var member = group.Members[0];

        var load = viewModel.PlaceInViewerCommand.ExecuteAsync(
            new PlaceInViewer(ViewerSide.Left, group.GroupId, member));
        await source.WaitForRequestCountAsync(1);
        workflow.Publish(ReviewData.WithSelection(
            workflow.Current.Review, [member.OccurrenceId], 10));

        var replacement = Assert.IsType<ScanOccurrenceViewModel>(viewModel.LeftViewer.Member);
        Assert.NotSame(member, replacement);
        Assert.Equal(member.OccurrenceId, replacement.OccurrenceId);
        Assert.True(viewModel.LeftViewer.IsMarked);
        var request = Assert.Single(source.Requests);
        Assert.False(request.CancellationToken.IsCancellationRequested);
        using var result = ReviewPreviewResult.Failure(
            member.ResolvedPath, PreviewAvailability.Unavailable, "kept result");
        source.Complete(0, result);
        await load;

        Assert.Equal("kept result", viewModel.LeftViewer.Preview?.StatusText);
        Assert.True(result.IsDisposed);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public async Task PreviewLoadFailureIsConvertedToUiSafeUnavailableEvidence(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "preview failed")!;
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(
            workflow.Scope, workflow, reviewPreviewSource: new ThrowingPreviewSource(exception));
        var group = viewModel.ReviewCategories[0].Groups[0];
        var member = group.Members[0];

        await viewModel.PlaceInViewerCommand.ExecuteAsync(
            new PlaceInViewer(ViewerSide.Right, group.GroupId, member));

        var preview = Assert.IsType<PreviewItemViewModel>(viewModel.RightViewer.Preview);
        Assert.StartsWith("Preview unavailable:", preview.StatusText,
            StringComparison.Ordinal);
        Assert.Same(member, viewModel.RightViewer.Member);
    }
}

internal sealed class ControllableReviewPreviewSource(bool autoComplete = false) : IReviewPreviewSource
{
    private readonly List<PendingPreviewRequest> requests = [];
    private TaskCompletionSource<bool> requestChanged = NewSignal();

    public IReadOnlyList<PendingPreviewRequest> Requests => requests;

    public Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ReviewPreviewResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Add(new(context, occurrence, completion, cancellationToken));
        requestChanged.TrySetResult(true);
        if (autoComplete)
            completion.SetResult(ReviewPreviewResult.Failure(
                occurrence.ResolvedPath, PreviewAvailability.Unavailable, "Unavailable"));
        return completion.Task;
    }

    public void Complete(int index, ReviewPreviewResult result) =>
        requests[index].Completion.SetResult(result);

    public async Task WaitForRequestCountAsync(int count)
    {
        while (requests.Count < count)
        {
            var signal = requestChanged;
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (ReferenceEquals(signal, requestChanged))
                requestChanged = NewSignal();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record PendingPreviewRequest(
    ScanRunContext Context,
    ScanFileOccurrence Occurrence,
    TaskCompletionSource<ReviewPreviewResult> Completion,
    CancellationToken CancellationToken);

internal sealed class ThrowingPreviewSource(Exception exception) : IReviewPreviewSource
{
    public Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken) => Task.FromException<ReviewPreviewResult>(exception);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
