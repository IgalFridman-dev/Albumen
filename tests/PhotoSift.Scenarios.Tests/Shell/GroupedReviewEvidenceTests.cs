using PhotoSift.App.Shell;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Windows.Preview;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class GroupedReviewEvidenceTests
{
    [Fact]
    public async Task OnlyTheActiveMemberLoadsAndAStaleCompletionCannotReplaceIt()
    {
        var source = new ControllableReviewPreviewSource();
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(
            workflow.Scope, workflow, reviewPreviewSource: source);
        var group = await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact);
        var first = group.Members[0];
        var second = group.Members[1];

        Assert.Empty(source.Requests);

        var firstNavigation = viewModel.OpenReviewMemberCommand.ExecuteAsync(first);
        await source.WaitForRequestCountAsync(1);
        var secondNavigation = viewModel.OpenReviewMemberCommand.ExecuteAsync(second);
        await source.WaitForRequestCountAsync(2);

        using var secondResult = ReviewPreviewResult.Failure(
            second.ResolvedPath, PreviewAvailability.Unavailable, "second unavailable");
        source.Complete(1, secondResult);
        await secondNavigation;

        Assert.Same(second, viewModel.ActiveReviewMember);
        Assert.Equal(second.ResolvedPath, viewModel.ActiveReviewPreview?.FullPath);

        using var firstResult = ReviewPreviewResult.Failure(
            first.ResolvedPath, PreviewAvailability.Unavailable, "first unavailable");
        source.Complete(0, firstResult);
        await firstNavigation;

        Assert.Same(second, viewModel.ActiveReviewMember);
        Assert.Equal(second.ResolvedPath, viewModel.ActiveReviewPreview?.FullPath);
        Assert.True(firstResult.IsDisposed);
        Assert.Collection(source.Requests,
            request => Assert.Equal(first.OccurrenceId, request.Occurrence.Token.OccurrenceId),
            request => Assert.Equal(second.OccurrenceId, request.Occurrence.Token.OccurrenceId));
    }

    [Fact]
    public async Task AbsentComparisonEvidenceUsesTheLiteralUnavailableAndNoRecommendation()
    {
        var source = new ControllableReviewPreviewSource(autoComplete: true);
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(
            workflow.Scope, workflow, reviewPreviewSource: source);
        var group = await OpenGroupAsync(viewModel, ReviewCategoryKind.Near);

        await viewModel.OpenReviewMemberCommand.ExecuteAsync(group.Members[0]);

        var evidence = Assert.IsType<ReviewComparisonEvidenceViewModel>(
            viewModel.ActiveReviewEvidence);
        Assert.Equal(group.Members[0].ResolvedPath, evidence.FullPath);
        Assert.Equal("Look-alike", evidence.Category);
        Assert.Equal("Unavailable", evidence.Dimensions);
        Assert.Equal("512 bytes", evidence.Size);
        Assert.Equal("Unavailable", evidence.RelevantDate);
        Assert.Equal("Unavailable", evidence.Confidence);
        Assert.Equal("Unavailable", evidence.Uncertainty);
        Assert.Equal("Unavailable", evidence.Transformations);
        Assert.Equal("Unavailable", evidence.Reason);
        Assert.Equal("PhotoSift doesn't mark photos for you. You choose what to remove.", evidence.Recommendation);
    }

    [Fact]
    public async Task ReopeningTheSameMemberCancelsTheOlderLoadAndPublishesOnlyTheNewest()
    {
        var source = new ControllableReviewPreviewSource();
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(
            workflow.Scope, workflow, reviewPreviewSource: source);
        var member = (await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact)).Members[0];

        var older = viewModel.OpenReviewMemberCommand.ExecuteAsync(member);
        await source.WaitForRequestCountAsync(1);
        var newer = viewModel.OpenReviewMemberCommand.ExecuteAsync(member);
        await source.WaitForRequestCountAsync(2);

        Assert.True(source.Requests[0].CancellationToken.IsCancellationRequested);
        using var newestResult = ReviewPreviewResult.Failure(
            member.ResolvedPath, PreviewAvailability.Unavailable, "newest result");
        source.Complete(1, newestResult);
        await newer;
        using var olderResult = ReviewPreviewResult.Failure(
            member.ResolvedPath, PreviewAvailability.Unavailable, "older result");
        source.Complete(0, olderResult);
        await older;

        Assert.Equal("newest result", viewModel.ActiveReviewPreview?.StatusText);
        Assert.True(olderResult.IsDisposed);
    }

    [Fact]
    public async Task SelectionRefreshRestartsThePreviewForTheLogicallyActiveReplacement()
    {
        var source = new ControllableReviewPreviewSource();
        var scope = new ReviewStaticScope();
        var workflow = new RecordingReviewWorkflow(
            scope.Current, ReviewData.CreateSnapshot());
        await using var viewModel = new ShellViewModel(
            scope, workflow, reviewPreviewSource: source);
        var member = (await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact)).Members[0];

        var originalLoad = viewModel.OpenReviewMemberCommand.ExecuteAsync(member);
        await source.WaitForRequestCountAsync(1);
        workflow.Publish(ReviewData.WithSelection(
            workflow.Current.Review, [member.OccurrenceId], 10));
        await source.WaitForRequestCountAsync(2);

        var replacement = Assert.IsType<ScanOccurrenceViewModel>(viewModel.ActiveReviewMember);
        Assert.NotSame(member, replacement);
        Assert.Equal(member.OccurrenceId, replacement.OccurrenceId);
        Assert.True(source.Requests[0].CancellationToken.IsCancellationRequested);
        using var replacementResult = ReviewPreviewResult.Failure(
            replacement.ResolvedPath, PreviewAvailability.Unavailable, "replacement result");
        source.Complete(1, replacementResult);
        await WaitForPreviewStatusAsync(viewModel, "replacement result");
        using var staleResult = ReviewPreviewResult.Failure(
            member.ResolvedPath, PreviewAvailability.Unavailable, "stale result");
        source.Complete(0, staleResult);
        await originalLoad;

        Assert.Equal("replacement result", viewModel.ActiveReviewPreview?.StatusText);
        Assert.Equal(replacement.OccurrenceId, source.Requests[1].Occurrence.Token.OccurrenceId);
        Assert.True(staleResult.IsDisposed);
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
        var member = (await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact)).Members[0];

        await viewModel.OpenReviewMemberCommand.ExecuteAsync(member);

        var preview = Assert.IsType<PreviewItemViewModel>(viewModel.ActiveReviewPreview);
        Assert.StartsWith("Preview unavailable:", preview.StatusText,
            StringComparison.Ordinal);
        Assert.Same(member, viewModel.ActiveReviewMember);
    }

    private static async Task<ReviewGroupViewModel> OpenGroupAsync(
        ShellViewModel viewModel,
        ReviewCategoryKind category)
    {
        var row = viewModel.ReviewCategories.Single(item => item.Category == category);
        await viewModel.OpenReviewCategoryCommand.ExecuteAsync(row);
        var group = Assert.Single(row.Groups);
        await viewModel.OpenReviewGroupCommand.ExecuteAsync(group);
        return group;
    }

    private static async Task WaitForPreviewStatusAsync(
        ShellViewModel viewModel,
        string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (viewModel.ActiveReviewPreview?.StatusText != expected)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Preview status did not become '{expected}'.");
            await Task.Delay(10);
        }
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
