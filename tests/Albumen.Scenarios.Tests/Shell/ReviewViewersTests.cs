using Albumen.App.Shell;
using Albumen.Domain.Review;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// 1.1.5: two viewers below the sets. Placing, clearing and swapping only change what the
// viewers show; Remove runs the set's own selection command.
public sealed class ReviewViewersTests
{
    [Fact]
    public async Task PlacingIntoAnEmptyViewerLoadsItsPreviewOnce()
    {
        var source = new ControllableReviewPreviewSource(autoComplete: true);
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow, reviewPreviewSource: source);
        var group = Set(viewModel, ReviewCategoryKind.Exact);

        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));

        var request = Assert.Single(source.Requests);
        Assert.Equal(group.Members[0].OccurrenceId, request.Occurrence.Token.OccurrenceId);
        Assert.Same(group.Members[0], viewModel.LeftViewer.Member);
        Assert.Equal(group.GroupId, viewModel.LeftViewer.GroupId);
        Assert.Equal(group.Members[0].ResolvedPath, viewModel.LeftViewer.Preview?.FullPath);
        Assert.Equal($"Left Viewer: {group.Members[0].FileName}", viewModel.LeftViewer.AutomationName);
        Assert.False(viewModel.RightViewer.HasPhoto);
        Assert.Equal(ReviewViewerSlot.EmptyText, viewModel.RightViewer.ItemStatus);
        Assert.Empty(workflow.SelectionCommands);
    }

    [Fact]
    public async Task PlacingAnotherPhotoReplacesItAndCancelsThePendingLoad()
    {
        var source = new ControllableReviewPreviewSource();
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow, reviewPreviewSource: source);
        var group = Set(viewModel, ReviewCategoryKind.Exact);

        var first = viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        await source.WaitForRequestCountAsync(1);
        var second = viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 1));
        await source.WaitForRequestCountAsync(2);

        Assert.True(source.Requests[0].CancellationToken.IsCancellationRequested);
        using var secondResult = ReviewPreviewResult.Failure(
            group.Members[1].ResolvedPath, PreviewAvailability.Unavailable, "second unavailable");
        source.Complete(1, secondResult);
        await second;
        using var firstResult = ReviewPreviewResult.Failure(
            group.Members[0].ResolvedPath, PreviewAvailability.Unavailable, "first unavailable");
        source.Complete(0, firstResult);
        await first;

        Assert.Same(group.Members[1], viewModel.LeftViewer.Member);
        Assert.Equal("second unavailable", viewModel.LeftViewer.Preview?.StatusText);
        Assert.True(firstResult.IsDisposed);
    }

    [Fact]
    public async Task APhotoIsInAtMostOneViewer()
    {
        var source = new ControllableReviewPreviewSource(autoComplete: true);
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow, reviewPreviewSource: source);
        var group = Set(viewModel, ReviewCategoryKind.Exact);
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        var preview = viewModel.LeftViewer.Preview;

        // The same photo in the same viewer changes nothing.
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        Assert.Single(source.Requests);
        Assert.Same(preview, viewModel.LeftViewer.Preview);

        // The Left Viewer's photo placed in the Right Viewer moves there with its preview.
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Right, group, 0));
        Assert.False(viewModel.LeftViewer.HasPhoto);
        Assert.Null(viewModel.LeftViewer.Preview);
        Assert.Same(group.Members[0], viewModel.RightViewer.Member);
        Assert.Same(preview, viewModel.RightViewer.Preview);
        Assert.Single(source.Requests);

        // Another photo replaces the Right Viewer's photo and leaves the Left Viewer empty.
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Right, group, 1));
        Assert.Same(group.Members[1], viewModel.RightViewer.Member);
        Assert.False(viewModel.LeftViewer.HasPhoto);
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public async Task AClickPlacesOnlyWhileTheLeftViewerIsEmpty()
    {
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow);
        var group = Set(viewModel, ReviewCategoryKind.Exact);

        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0, onlyIfEmpty: true));
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 1, onlyIfEmpty: true));

        Assert.Same(group.Members[0], viewModel.LeftViewer.Member);
        Assert.False(viewModel.RightViewer.HasPhoto);
    }

    [Fact]
    public async Task ClearingAViewerLeavesTheMarkOfItsPhoto()
    {
        var fixture = ReviewShellFixture.Ready(selected: [1]);
        var viewModel = fixture.ViewModel;
        var group = Set(viewModel, ReviewCategoryKind.Exact);
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        Assert.True(viewModel.LeftViewer.IsMarked);

        await viewModel.ClearViewerCommand.ExecuteAsync(ViewerSide.Left);

        Assert.False(viewModel.LeftViewer.HasPhoto);
        Assert.Null(viewModel.LeftViewer.GroupId);
        Assert.False(viewModel.LeftViewer.ToggleRemoveCommand.CanExecute(null));
        Assert.Empty(fixture.Workflow.SelectionCommands);
        Assert.True(Set(viewModel, ReviewCategoryKind.Exact).Members[0].IsSelected);
    }

    [Fact]
    public async Task SwappingKeepsBothPreviewsAndBothMarks()
    {
        var fixture = GroupedWpfFixture.Create(review: _ => ReviewData.CreateSnapshot(selected: [1]));
        var viewModel = fixture.ViewModel;
        var group = Set(viewModel, ReviewCategoryKind.Exact);
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Right, group, 3));
        var leftPreview = viewModel.LeftViewer.Preview;
        var rightPreview = viewModel.RightViewer.Preview;

        await viewModel.SwapViewersCommand.ExecuteAsync();

        Assert.Equal(4, viewModel.LeftViewer.Member?.OccurrenceId);
        Assert.Same(rightPreview, viewModel.LeftViewer.Preview);
        Assert.False(viewModel.LeftViewer.IsMarked);
        Assert.Equal(1, viewModel.RightViewer.Member?.OccurrenceId);
        Assert.Same(leftPreview, viewModel.RightViewer.Preview);
        Assert.True(viewModel.RightViewer.IsMarked);
        Assert.Equal(2, fixture.Preview.Requests.Count);

        // With one viewer empty a swap is a move.
        await viewModel.ClearViewerCommand.ExecuteAsync(ViewerSide.Left);
        await viewModel.SwapViewersCommand.ExecuteAsync();

        Assert.Equal(1, viewModel.LeftViewer.Member?.OccurrenceId);
        Assert.Same(leftPreview, viewModel.LeftViewer.Preview);
        Assert.False(viewModel.RightViewer.HasPhoto);
        Assert.Equal(2, fixture.Preview.Requests.Count);
        Assert.Empty(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public async Task TheRemoveButtonRunsTheSetsToggleAndFollowsTheMark()
    {
        var fixture = ReviewShellFixture.Ready();
        var viewModel = fixture.ViewModel;
        var group = Set(viewModel, ReviewCategoryKind.Exact);
        var member = group.Members[0];
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        Assert.True(viewModel.LeftViewer.CanRemove);
        Assert.Equal("Remove this photo", viewModel.LeftViewer.RemoveButtonText);
        Assert.Equal("Remove the photo in the Left Viewer", viewModel.LeftViewer.RemoveAutomationName);

        await viewModel.LeftViewer.ToggleRemoveCommand.ExecuteAsync();

        var command = Assert.Single(fixture.Workflow.SelectionCommands);
        Assert.Equal(SelectionCommandKind.Toggle, command.Kind);
        Assert.Equal(group.GroupId, command.Pin.GroupId);
        Assert.Equal([member.OccurrenceId], command.OccurrenceIds);
        // The projection re-created the set's rows; the viewer now holds the marked row.
        var marked = Assert.IsType<ScanOccurrenceViewModel>(viewModel.LeftViewer.Member);
        Assert.NotSame(member, marked);
        Assert.Equal(member.OccurrenceId, marked.OccurrenceId);
        Assert.True(viewModel.LeftViewer.IsMarked);
        Assert.Equal("Unmark this photo", viewModel.LeftViewer.RemoveButtonText);
        Assert.Equal("Unmark the photo in the Left Viewer", viewModel.LeftViewer.RemoveAutomationName);
        Assert.Equal(marked.ItemStatus, viewModel.LeftViewer.ItemStatus);

        await viewModel.LeftViewer.ToggleRemoveCommand.ExecuteAsync();

        Assert.Equal(2, fixture.Workflow.SelectionCommands.Count);
        Assert.Equal(SelectionCommandKind.Toggle, fixture.Workflow.SelectionCommands[1].Kind);
        Assert.False(viewModel.LeftViewer.IsMarked);
        Assert.Equal("Remove this photo", viewModel.LeftViewer.RemoveButtonText);
    }

    [Fact]
    public async Task ThePhotoThatCannotBeChangedInItsSetCannotBeChangedFromAViewer()
    {
        var fixture = ReviewShellFixture.Ready();
        var viewModel = fixture.ViewModel;
        var group = Set(viewModel, ReviewCategoryKind.Exact);
        // Photo 2 is still being checked again, so its set's control is disabled too.
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 1));

        Assert.False(group.Members[1].CanSelect);
        Assert.False(viewModel.LeftViewer.CanRemove);
        Assert.False(viewModel.LeftViewer.ToggleRemoveCommand.CanExecute(null));
        await viewModel.LeftViewer.ToggleRemoveCommand.ExecuteAsync();
        Assert.Empty(fixture.Workflow.SelectionCommands);
        Assert.False(viewModel.RightViewer.ToggleRemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task AViewerEmptiesWhenItsPhotoLeavesReview()
    {
        var fixture = ReviewShellFixture.Ready();
        var viewModel = fixture.ViewModel;
        var group = Set(viewModel, ReviewCategoryKind.Exact);
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, group, 0));
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Right, group, 3));
        var review = fixture.Workflow.Current.Review;
        var reviewed = review.Groups[0];

        // A cleanup moved photo 1 to the Recycle Bin; photo 4 is still in its set.
        fixture.Workflow.Publish(review with
        {
            SelectionRevision = 10,
            Groups = [reviewed with { Members = reviewed.Members.RemoveAt(0) }],
        });

        Assert.False(viewModel.LeftViewer.HasPhoto);
        Assert.Equal(4, viewModel.RightViewer.Member?.OccurrenceId);

        // A new Review session empties the viewers even where its set ids are the same.
        fixture.Workflow.Publish(fixture.Workflow.Current.Review with
        {
            Pin = new ReviewSessionPin(Guid.Parse("21000000-0000-0000-0000-000000000022"), review.Pin!.Scan),
            SelectionRevision = 1,
        });

        Assert.False(viewModel.RightViewer.HasPhoto);
    }

    [Fact]
    public async Task ViewerSideOfNamesTheViewerOfThatPhotoInThatSet()
    {
        var workflow = new GroupedReviewWorkflow(includeSecondFailure: false);
        await using var viewModel = new ShellViewModel(workflow.Scope, workflow);
        var exact = Set(viewModel, ReviewCategoryKind.Exact);
        var near = Set(viewModel, ReviewCategoryKind.Near);

        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, exact, 0));
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Right, near, 1));

        Assert.Equal(ViewerSide.Left, viewModel.ViewerSideOf(exact.Members[0].OccurrenceId, exact.GroupId));
        Assert.Equal(ViewerSide.Right, viewModel.ViewerSideOf(near.Members[1].OccurrenceId, near.GroupId));
        Assert.Null(viewModel.ViewerSideOf(exact.Members[1].OccurrenceId, exact.GroupId));
        Assert.Null(viewModel.ViewerSideOf(exact.Members[0].OccurrenceId, near.GroupId));
    }

    [Fact]
    public async Task ACouldNotCheckPhotoStaysInItsViewerAcrossProjections()
    {
        var scope = new ReviewStaticScope();
        var workflow = new PublishingReviewWorkflow(GroupedReviewData.Snapshot(scope.Current, includeSecondFailure: false));
        await using var viewModel = new ShellViewModel(scope, workflow);
        var failed = Set(viewModel, ReviewCategoryKind.CouldNotAnalyze);
        await viewModel.PlaceInViewerCommand.ExecuteAsync(Place(ViewerSide.Left, failed, 0));

        workflow.Publish(workflow.Current with
        {
            Review = workflow.Current.Review with { SelectionRevision = 10 },
        });

        Assert.Equal(GroupedReviewFixture.FailedOccurrenceId, viewModel.LeftViewer.Member?.OccurrenceId);
        Assert.Equal(Guid.Empty, viewModel.LeftViewer.GroupId);
        Assert.False(viewModel.LeftViewer.CanRemove);
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

    private static ReviewGroupViewModel Set(ShellViewModel viewModel, ReviewCategoryKind category) =>
        viewModel.ReviewCategories.Single(row => row.Category == category).Groups[0];

    private static PlaceInViewer Place(ViewerSide side, ReviewGroupViewModel group, int index,
        bool onlyIfEmpty = false) =>
        new(side, group.GroupId, group.Members[index], onlyIfEmpty);

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
