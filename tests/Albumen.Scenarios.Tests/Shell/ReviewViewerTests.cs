using Albumen.App.Shell;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class ReviewViewerTests
{
    [Fact]
    public async Task PreviousAndNextStepThroughTheOpenSetAndStopAtItsEnds()
    {
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewPreviewSource: new ControllableReviewPreviewSource(autoComplete: true));
        var group = await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact);

        Assert.False(viewModel.ShowPreviousReviewMemberCommand.CanExecute(null));
        Assert.False(viewModel.ShowNextReviewMemberCommand.CanExecute(null));

        await viewModel.OpenReviewMemberCommand.ExecuteAsync(group.Members[0]);
        Assert.False(viewModel.ShowPreviousReviewMemberCommand.CanExecute(null));
        Assert.True(viewModel.ShowNextReviewMemberCommand.CanExecute(null));

        await viewModel.ShowNextReviewMemberCommand.ExecuteAsync(null);
        Assert.Same(group.Members[1], viewModel.ActiveReviewMember);
        Assert.Equal(group.Members[1].ResolvedPath, viewModel.ActiveReviewPreview?.FullPath);
        Assert.True(viewModel.ShowPreviousReviewMemberCommand.CanExecute(null));
        Assert.False(viewModel.ShowNextReviewMemberCommand.CanExecute(null));

        await viewModel.ShowPreviousReviewMemberCommand.ExecuteAsync(null);
        Assert.Same(group.Members[0], viewModel.ActiveReviewMember);
    }

    [Fact]
    public async Task CompareLoadsASecondPhotoAndEndsWhenThatPhotoIsOpenedOrTheSetIsLeft()
    {
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewPreviewSource: new ControllableReviewPreviewSource(autoComplete: true));
        var group = await OpenGroupAsync(viewModel, ReviewCategoryKind.Near);
        var first = group.Members[0];
        var second = group.Members[1];
        await viewModel.OpenReviewMemberCommand.ExecuteAsync(first);

        await viewModel.CompareReviewMemberCommand.ExecuteAsync(second);
        Assert.True(viewModel.IsComparingReviewMembers);
        Assert.Same(second, viewModel.ComparedReviewMember);
        Assert.Equal(second.ResolvedPath, viewModel.ComparedReviewPreview?.FullPath);
        Assert.Same(first, viewModel.ActiveReviewMember);

        await viewModel.CloseCompareCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsComparingReviewMembers);
        Assert.Null(viewModel.ComparedReviewPreview);
        Assert.False(viewModel.CloseCompareCommand.CanExecute(null));

        await viewModel.CompareReviewMemberCommand.ExecuteAsync(second);
        await viewModel.OpenReviewMemberCommand.ExecuteAsync(second);
        Assert.False(viewModel.IsComparingReviewMembers);

        await viewModel.CompareReviewMemberCommand.ExecuteAsync(first);
        Assert.True(viewModel.IsComparingReviewMembers);
        await viewModel.ReviewBackCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsComparingReviewMembers);
        Assert.Null(viewModel.ComparedReviewPreview);
    }

    [Fact]
    public async Task ComparingTheOpenPhotoWithItselfDoesNothing()
    {
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewPreviewSource: new ControllableReviewPreviewSource(autoComplete: true));
        var group = await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact);
        await viewModel.OpenReviewMemberCommand.ExecuteAsync(group.Members[0]);

        await viewModel.CompareReviewMemberCommand.ExecuteAsync(group.Members[0]);

        Assert.False(viewModel.IsComparingReviewMembers);
    }

    [Fact]
    public async Task HeaderAndSelectionBarUsePlainWords()
    {
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow);

        Assert.Equal("3 sets to review · 1 file couldn't be checked", viewModel.ReviewResultsSummary);
        Assert.Equal("Nothing marked for removal yet", viewModel.ReviewSelectionBarText);
        Assert.All(viewModel.ReviewCategories.Single(row => row.Category == ReviewCategoryKind.Exact).Groups,
            group => Assert.Equal("Nothing marked yet", group.SelectionStatus));
    }

    [Fact]
    public async Task CardsShowingTheSamePhotoShareOneThumbnailLoad()
    {
        var thumbnails = new ControllableReviewPreviewSource();
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewThumbnailSource: thumbnails);
        var member = (await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact)).Members[0];
        var loader = Assert.IsType<ReviewThumbnailLoader>(viewModel.ReviewThumbnails);

        var firstCard = loader.LoadAsync(member, CancellationToken.None);
        var secondCard = loader.LoadAsync(member, CancellationToken.None);
        await thumbnails.WaitForRequestCountAsync(1);
        using var result = ReviewPreviewResult.Failure(
            member.ResolvedPath, PreviewAvailability.Unavailable, "Preview unavailable: test.");
        thumbnails.Complete(0, result);

        var first = await firstCard;
        var second = await secondCard;
        Assert.Single(thumbnails.Requests);
        Assert.Same(first, second);
        Assert.Equal("Preview unavailable: test.", first?.Status);
        Assert.Equal(0, loader.PendingCount);
    }

    [Fact]
    public async Task AThumbnailLoadIsCanceledOnlyWhenNoCardWaitsForItAnyMore()
    {
        var thumbnails = new ControllableReviewPreviewSource();
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewThumbnailSource: thumbnails);
        var member = (await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact)).Members[0];
        var loader = Assert.IsType<ReviewThumbnailLoader>(viewModel.ReviewThumbnails);
        using var firstCard = new CancellationTokenSource();
        using var secondCard = new CancellationTokenSource();

        var first = loader.LoadAsync(member, firstCard.Token);
        var second = loader.LoadAsync(member, secondCard.Token);
        await thumbnails.WaitForRequestCountAsync(1);
        var request = Assert.Single(thumbnails.Requests);

        await firstCard.CancelAsync();
        Assert.Null(await first);
        Assert.False(request.CancellationToken.IsCancellationRequested);

        await secondCard.CancelAsync();
        Assert.Null(await second);
        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, loader.PendingCount);
    }

    [Fact]
    public async Task IdenticalCopiesNeverLoadThumbnailsForASuggestion()
    {
        var thumbnails = new ControllableReviewPreviewSource(autoComplete: true);
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewThumbnailSource: thumbnails);

        await OpenGroupAsync(viewModel, ReviewCategoryKind.Exact);

        Assert.Empty(thumbnails.Requests);
        Assert.Null(viewModel.ActiveReviewSuggestion);
    }

    [Fact]
    public async Task LookAlikeSetWithoutPixelSizesGetsNoSuggestion()
    {
        var thumbnails = new ControllableReviewPreviewSource(autoComplete: true);
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow,
            reviewThumbnailSource: thumbnails);

        var group = await OpenGroupAsync(viewModel, ReviewCategoryKind.Near);

        // The source answers at once, so the whole suggestion pass has finished here: the
        // first photo without a pixel size ends it.
        var request = Assert.Single(thumbnails.Requests);
        Assert.Equal(group.Members[0].OccurrenceId, request.Occurrence.Token.OccurrenceId);
        Assert.Null(viewModel.ActiveReviewSuggestion);
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
}

public sealed class SuggestedBestPhotoTests
{
    [Fact]
    public void ThePhotoWithTheMostPixelsIsSuggested()
    {
        var pick = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 2_000_000),
            new(2, 2000, 1500, 3_000_000),
        ]);

        Assert.Equal(new SuggestedBest(1, "most pixels"), pick);
    }

    [Fact]
    public void TheReasonSaysWhenTheBiggestPhotoIsAlsoTheLargestFile()
    {
        var pick = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 3_000_000),
            new(2, 2000, 1500, 2_000_000),
        ]);

        Assert.Equal(new SuggestedBest(1, "most pixels and largest file"), pick);
    }

    [Fact]
    public void EqualPixelSizesAreSettledByTheLargestFile()
    {
        var pick = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 2_000_000),
            new(2, 3000, 4000, 2_500_000),
            new(3, 1000, 1000, 9_000_000),
        ]);

        Assert.Equal(new SuggestedBest(2, "largest file at the highest pixel size"), pick);
    }

    [Fact]
    public void AClearlySharpestPhotoLeadsEvenOverMorePixels()
    {
        var pick = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 3_000_000, Sharpness: 120),
            new(2, 2000, 1500, 2_000_000, Sharpness: 900),
        ]);

        Assert.Equal(new SuggestedBest(2, "sharpest"), pick);
    }

    [Fact]
    public void TheReasonListsEverythingTheSharpestPhotoAlsoLeads()
    {
        var pick = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 3_000_000, Sharpness: 900),
            new(2, 4000, 3000, 2_000_000, Sharpness: 300),
            new(3, 2000, 1500, 1_000_000, Sharpness: 200),
        ]);
        var all = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 3_000_000, Sharpness: 900),
            new(2, 2000, 1500, 2_000_000, Sharpness: 300),
        ]);

        Assert.Equal(new SuggestedBest(1, "sharpest and largest file"), pick);
        Assert.Equal(new SuggestedBest(1, "sharpest, most pixels and largest file"), all);
    }

    [Fact]
    public void SimilarSharpnessFallsBackToPixelsAndFileSize()
    {
        var pick = SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 2_000_000, Sharpness: 800),
            new(2, 2000, 1500, 3_000_000, Sharpness: 900),
        ]);

        Assert.Equal(new SuggestedBest(1, "most pixels"), pick);
    }

    [Fact]
    public void APhotoTheSetCallsBlurrierIsNeverSuggested()
    {
        Assert.Null(SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, 3_000_000, Sharpness: 150),
            new(2, 2000, 1500, 2_000_000, Sharpness: 900),
            new(3, 2000, 1500, 1_000_000, Sharpness: 800),
        ]));
    }

    [Theory]
    [InlineData(2_000_000L, 2_000_000L)]
    [InlineData(2_000_000L, null)]
    public void AnUnsettledTieSuggestsNothing(long first, long? second)
    {
        Assert.Null(SuggestedBestPhoto.Pick([
            new(1, 4000, 3000, first),
            new(2, 4000, 3000, second),
        ]));
    }

    [Fact]
    public void SetsOutsideTheSupportedSizeSuggestNothing()
    {
        Assert.Null(SuggestedBestPhoto.Pick([new(1, 10, 10, 1)]));
        var tooMany = Enumerable.Range(1, SuggestedBestPhoto.MaximumSetSize + 1)
            .Select(index => new PhotoFacts(index, index, index, index))
            .ToArray();
        Assert.Null(SuggestedBestPhoto.Pick(tooMany));
    }
}
