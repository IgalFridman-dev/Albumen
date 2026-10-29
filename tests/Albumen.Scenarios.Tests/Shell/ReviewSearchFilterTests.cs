using System.Collections.Immutable;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Application.Shell;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// E4-S4 and 1.1.5 (PM-019, PM-022): the photo search is a projection. It narrows Review to the sets
// with a match, shows each set whole and best match first, lists matching photos in no set as
// Other matches, and never changes a set, a mark or a cleanup.
public sealed class ReviewSearchFilterTests
{
    [Theory]
    [InlineData(SemanticState.Idle, false, "Search isn't ready for this Scan. Scan again to search photos by what they show.")]
    [InlineData(SemanticState.Indexing, false, "Getting photos ready for search: 3 of 6 ready.")]
    [InlineData(SemanticState.Ready, true, "Describe what's in a photo, for example “at sea”, and press Enter.")]
    [InlineData(SemanticState.Empty, false, "No photo in this Scan could be prepared for search.")]
    [InlineData(SemanticState.Unavailable, false, "Search isn't installed: The model file text.onnx is not installed. Everything else in Review works.")]
    [InlineData(SemanticState.Failed, false, "Search isn't available for this Scan: The photo search data could not be saved. Everything else in Review works.")]
    public void EachSearchStateHasItsOwnWordsAndOnlyReadyCanSearch(SemanticState state, bool canSearch, string text)
    {
        var fixture = new SearchFixture(index: new(state, 6, 3, 3, SearchFixture.Profile.Fingerprint,
            state switch
            {
                SemanticState.Failed => "The photo search data could not be saved.",
                SemanticState.Unavailable => "The model file text.onnx is not installed.",
                _ => "",
            }));

        Assert.Equal(text, fixture.Search.StatusText);
        Assert.Equal(canSearch, fixture.Search.CanSearch);
        Assert.Equal(canSearch, fixture.Search.SearchCommand.CanExecute(null));
        Assert.False(fixture.Search.IsActive);
        // The bar's placeholder says why search can't run; ready, it invites a search and the
        // status line under the bar stays hidden.
        Assert.Equal(canSearch ? "Find photos by what they show" : text, fixture.Search.PlaceholderText);
        Assert.Equal(!canSearch, fixture.Search.HasStatus);
    }

    [Fact]
    public void ReadySaysItSearchesOnlyThisScanAndDisclosesPhotosThatCouldNotBePrepared()
    {
        Assert.Equal("Searches only the 6 photos in this Scan.", new SearchFixture().Search.ScopeText);
        Assert.Equal("Searches only this Scan: 4 of 6 photos could be prepared, so some photos can't be found.",
            new SearchFixture(index: SearchFixture.Ready with { Indexed = 4 }).Search.ScopeText);
    }

    [Fact]
    public async Task APromptSaysHowManyPhotosOfTheScanMatch()
    {
        var fixture = new SearchFixture();

        await fixture.SearchAsync("  at   sea ");

        Assert.True(fixture.Search.IsActive);
        Assert.Equal("at sea", fixture.Search.Filter?.Prompt);
        Assert.Equal("5 likely matches in this Scan for “at sea”, most likely first", fixture.Search.StatusText);

        // After a cleanup the moved photo leaves every set but stays in the Scan: it is not counted.
        fixture.Publish(group => group with
        {
            Members = [.. group.Members.Where(member => member.Occurrence.Token.OccurrenceId != 12)],
        });

        Assert.Equal("4 likely matches in this Scan for “at sea”, most likely first", fixture.Search.StatusText);
    }

    [Fact]
    public async Task NoMatchSaysSoForThisScanOnly()
    {
        var fixture = new SearchFixture();

        await fixture.SearchAsync("nothing");

        Assert.True(fixture.Search.IsActive);
        Assert.Equal("No likely matches in this Scan for “nothing”. Try other words.", fixture.Search.StatusText);
    }

    [Fact]
    public async Task ATooLongPromptIsRejectedAndNothingIsSearched()
    {
        var fixture = new SearchFixture();

        await fixture.SearchAsync(new string('a', LikelyMatchesQuery.MaximumPromptLength + 1));

        Assert.False(fixture.Search.IsActive);
        Assert.Equal("Use at most 60 characters. This one has 61.", fixture.Search.StatusText);
        Assert.Empty(fixture.Encoder.Prompts);
    }

    [Fact]
    public async Task AnEmptyPromptClearsTheFilter()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");

        await fixture.SearchAsync("   ");

        Assert.False(fixture.Search.IsActive);
        Assert.Null(fixture.Search.Filter);
        Assert.Equal(4, fixture.ViewModel.ReviewCategories.Length);
        Assert.Empty(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public async Task ANewScanClearsTheFilter()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");
        Assert.Equal(5, fixture.ViewModel.ReviewCategories.Length);

        // Review still holds the old Scan's sets, so only the search changes here.
        var scan = fixture.Workflow.Current.Scan;
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = scan with { Pin = scan.Pin! with { ScanId = Guid.NewGuid() } },
        });

        Assert.Equal(string.Empty, fixture.Search.Prompt);
        Assert.False(fixture.Search.IsActive);
        Assert.Null(fixture.Search.Filter);
        Assert.Equal(4, fixture.ViewModel.ReviewCategories.Length);
        Assert.DoesNotContain(fixture.ViewModel.ReviewCategories, category => category.Category == ReviewCategoryKind.OtherMatches);
    }

    [Fact]
    public async Task MarksSurviveSearchReSearchClearAndCleanup()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");
        // Photo 21 is marked from its Maybe-similar set while the search shows it.
        await Member(fixture, ReviewCategoryKind.Uncertain, 21).ToggleSelectionCommand!.ExecuteAsync();
        var toggle = Assert.Single(fixture.Workflow.SelectionCommands);
        Assert.Equal([21L], toggle.OccurrenceIds);
        fixture.Mark(GroupedReviewData.UncertainGroupId, 21);

        await fixture.SearchAsync("lone");
        Assert.Empty(Shown(fixture, ReviewCategoryKind.Uncertain));
        Assert.Equal("Move 2 photos to the Recycle Bin?", fixture.ViewModel.ApprovalTitle);
        await fixture.Search.ShowAllCommand.ExecuteAsync();

        Assert.True(Member(fixture, ReviewCategoryKind.Exact, 1).IsSelected);
        Assert.True(Member(fixture, ReviewCategoryKind.Uncertain, 21).IsSelected);
        Assert.Equal("Move 2 photos to the Recycle Bin?", fixture.ViewModel.ApprovalTitle);

        // A cleanup moved photo 2 and closed its set; searched again, the mark on 21 is still shown.
        await fixture.SearchAsync("at sea");
        fixture.Publish(group => group with
        {
            Closed = group.Candidate.GroupId == GroupedReviewData.ExactGroupId,
            Members = [.. group.Members.Where(member => member.Occurrence.Token.OccurrenceId != 2)],
        });

        Assert.True(Member(fixture, ReviewCategoryKind.Uncertain, 21).IsSelected);
        Assert.Single(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public async Task TheSurvivorRuleAndApprovalAreTheSameWithAndWithoutAFilter()
    {
        var plain = new SearchFixture();
        var filtered = new SearchFixture();
        await filtered.SearchAsync("at sea");

        // Photo 2 does not match, but its set does, so the set shows whole and 2 can be marked.
        var unfiltered = Member(plain, ReviewCategoryKind.Exact, 2);
        var shown = Member(filtered, ReviewCategoryKind.Exact, 2);
        Assert.False(shown.MatchesSearch);
        Assert.Equal(unfiltered.CanSelect, shown.CanSelect);
        Assert.Equal(unfiltered.EligibilityText, shown.EligibilityText);
        Assert.Equal(plain.ViewModel.CanApproveReview, filtered.ViewModel.CanApproveReview);
        Assert.Equal(plain.ViewModel.ApprovalTitle, filtered.ViewModel.ApprovalTitle);

        await unfiltered.ToggleSelectionCommand!.ExecuteAsync();
        await shown.ToggleSelectionCommand!.ExecuteAsync();

        // The same command reaches the workflow, which applies the survivor rule as always.
        var expected = Assert.Single(plain.Workflow.SelectionCommands);
        var actual = Assert.Single(filtered.Workflow.SelectionCommands);
        Assert.Equal(expected.Pin, actual.Pin);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.OccurrenceIds, actual.OccurrenceIds);
    }

    [Fact]
    public async Task TheApprovalCountsEveryMarkedPhotoAndSaysHowManyAreHidden()
    {
        var fixture = new SearchFixture();
        Assert.Equal(string.Empty, fixture.ViewModel.ApprovalHiddenMarkedText);

        await fixture.SearchAsync("lone");
        Assert.Equal("Move 1 photo to the Recycle Bin?", fixture.ViewModel.ApprovalTitle);
        Assert.Equal("It is hidden by the search.", fixture.ViewModel.ApprovalHiddenMarkedText);

        // A mark made while the search is on is projected with the search applied.
        fixture.Mark(GroupedReviewData.UncertainGroupId, 21);

        Assert.Equal("Move 2 photos to the Recycle Bin?", fixture.ViewModel.ApprovalTitle);
        Assert.Equal("2 of them are hidden by the search.", fixture.ViewModel.ApprovalHiddenMarkedText);
        Assert.True(fixture.ViewModel.HasApprovalHiddenMarked);
        Assert.Equal([1L, 21L], fixture.ViewModel.ApprovalMarkedPhotos.Select(photo => photo.OccurrenceId));
        Assert.EndsWith(" · 2 hidden by the search", fixture.ViewModel.ReviewSelectionBarText, StringComparison.Ordinal);
        Assert.True(fixture.ViewModel.CanApproveReview);

        await fixture.SearchAsync("at sea");
        Assert.Equal(string.Empty, fixture.ViewModel.ApprovalHiddenMarkedText);
        Assert.False(fixture.ViewModel.HasApprovalHiddenMarked);
        Assert.DoesNotContain("hidden", fixture.ViewModel.ReviewSelectionBarText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASetWithOneMatchShowsAllItsMembersAndLabelsOnlyTheMatch()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");

        var exact = Category(fixture, ReviewCategoryKind.Exact);
        var set = Assert.Single(exact.Groups);
        Assert.Equal([1L, 2L], set.Members.Select(member => member.OccurrenceId));
        Assert.Equal([true, false], set.Members.Select(member => member.MatchesSearch));
        Assert.Equal("1 of 1 set", exact.CountText);
        Assert.Equal("Identical copies: 1 of 1 set", exact.AutomationName);

        await fixture.Search.ShowAllCommand.ExecuteAsync();

        Assert.Equal("1 set", Category(fixture, ReviewCategoryKind.Exact).CountText);
        Assert.All(fixture.ViewModel.ReviewCategories.SelectMany(category => category.Groups).SelectMany(group => group.Members),
            member => Assert.False(member.MatchesSearch));
    }

    [Fact]
    public async Task SetsAreOrderedByTheirBestMatchThenBySetId()
    {
        // Four identical-copies sets of photos (1, 2), (3, 4), (5, 6) and (7, 8): the third holds the
        // best match, the first and the last tie, and the second has none.
        var scores = new Dictionary<long, (float AtSea, float Lone)>
        {
            [1] = (0.01f, 0), [2] = (0.29f, 0), [3] = (0.01f, 0), [4] = (0.01f, 0),
            [5] = (0.3f, 0), [6] = (0.01f, 0), [7] = (0.29f, 0), [8] = (0.01f, 0),
        };
        var orders = new List<long[]>();
        for (var run = 0; run < 2; run++)
        {
            var fixture = new SearchFixture(snapshot: GroupedReviewData.LargeSnapshot(4), scores: scores);
            await fixture.SearchAsync("at sea");

            var exact = Category(fixture, ReviewCategoryKind.Exact);
            Assert.Equal("3 of 4 sets", exact.CountText);
            // A set keeps its number in its kind while the search orders the kind.
            Assert.Equal(["Set 3: 2 photos", "Set 1: 2 photos", "Set 4: 2 photos"], exact.Groups.Select(group => group.Summary));
            orders.Add([.. exact.Groups.Select(group => group.Members[0].OccurrenceId)]);
        }

        Assert.Equal([5L, 1L, 7L], orders[0]);
        Assert.Equal(orders[0], orders[1]);
    }

    [Fact]
    public async Task OtherMatchesHoldsExactlyTheMatchingPhotosInNoOpenSet()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");

        var other = Category(fixture, ReviewCategoryKind.OtherMatches);
        Assert.Equal("Other matches", other.Name);
        Assert.Equal("1 photo", other.CountText);
        Assert.Equal([50L], Shown(fixture, ReviewCategoryKind.OtherMatches));

        // After a cleanup the Look-alike set is closed with photo 12 still in it, and photo 22 was
        // moved to the Recycle Bin: a photo in a closed set is in no open set, a moved one is gone.
        fixture.Publish(group => group.Candidate.GroupId == GroupedReviewData.NearGroupId
            ? group with { Closed = true }
            : group with { Members = [.. group.Members.Where(member => member.Occurrence.Token.OccurrenceId != 22)] });

        Assert.Equal([12L, 50L], Shown(fixture, ReviewCategoryKind.OtherMatches));
        Assert.Empty(Category(fixture, ReviewCategoryKind.Near).Groups);
        Assert.DoesNotContain(22L, fixture.ViewModel.ReviewCategories.SelectMany(category => ShownIds(category)));
        // Photo 1 is still in open sets, so Other matches does not list it again.
        Assert.DoesNotContain(1L, Shown(fixture, ReviewCategoryKind.OtherMatches));
    }

    [Fact]
    public async Task AnOtherMatchOpensInAViewerWithoutRemove()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");
        var other = Category(fixture, ReviewCategoryKind.OtherMatches);
        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(other);
        var set = Assert.Single(other.Groups);
        var photo = Assert.Single(set.Members);
        Assert.Equal(ScanOccurrenceViewModel.NotInAnySetText, photo.EligibilityText);
        Assert.StartsWith("Not in any set: nothing to decide; ", photo.ItemStatus, StringComparison.Ordinal);
        Assert.Null(photo.ToggleSelectionCommand);
        Assert.False(photo.CanSelect);
        Assert.True(photo.MatchesSearch);

        // A click shows it in the empty Left Viewer, read-only: no Remove, nothing to mark.
        await fixture.ViewModel.PlaceInViewerCommand.ExecuteAsync(
            new PlaceInViewer(ViewerSide.Left, set.GroupId, photo, OnlyIfEmpty: true));

        var viewer = fixture.ViewModel.LeftViewer;
        Assert.Equal(50, viewer.Member?.OccurrenceId);
        Assert.True(viewer.IsReadOnly);
        Assert.False(viewer.HasRemove);
        Assert.False(viewer.CanRemove);
        Assert.False(viewer.ToggleRemoveCommand.CanExecute(null));
        Assert.Equal(ScanOccurrenceViewModel.NotInAnySetText, viewer.ReadOnlyText);
        Assert.StartsWith(ScanOccurrenceViewModel.NotInAnySetText, viewer.ItemStatus, StringComparison.Ordinal);
        await viewer.ToggleRemoveCommand.ExecuteAsync();
        Assert.Empty(fixture.Workflow.SelectionCommands);

        // It stays in its viewer across projections while the search lists it, and leaves with
        // the search, since Review then lists it nowhere.
        fixture.Mark(GroupedReviewData.UncertainGroupId, 21);
        Assert.Equal(50, fixture.ViewModel.LeftViewer.Member?.OccurrenceId);
        await fixture.Search.ShowAllCommand.ExecuteAsync();
        Assert.False(fixture.ViewModel.LeftViewer.HasPhoto);
    }

    [Fact]
    public async Task NoMatchSaysSoAndLeavesTheClearButton()
    {
        var fixture = new SearchFixture();

        await fixture.SearchAsync("nothing");

        Assert.All(fixture.ViewModel.ReviewCategories, category => Assert.Equal(0, category.GroupCount));
        Assert.Equal("No photos match “nothing”", fixture.ViewModel.ReviewSearchEmptyText);
        Assert.True(fixture.ViewModel.HasReviewSearchEmpty);
        Assert.True(fixture.Search.ShowAllCommand.CanExecute(null));

        await fixture.Search.ShowAllCommand.ExecuteAsync();

        Assert.False(fixture.ViewModel.HasReviewSearchEmpty);
        Assert.Equal(1, Category(fixture, ReviewCategoryKind.Exact).GroupCount);
    }

    // A search that leaves the open kind without a set while another kind has matches says so in
    // the gallery, instead of leaving it blank.
    [Fact]
    public async Task AnOpenKindWithoutAMatchSaysOtherKindsHaveMatches()
    {
        var fixture = new SearchFixture();
        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(Category(fixture, ReviewCategoryKind.Exact));
        Assert.False(fixture.ViewModel.HasReviewKindEmpty);

        await fixture.SearchAsync("lone");

        Assert.Equal(ReviewCategoryKind.Exact, fixture.ViewModel.ActiveReviewCategory?.Category);
        Assert.Equal(0, fixture.ViewModel.ActiveReviewCategory?.GroupCount);
        Assert.Equal("No photos in Identical copies match “lone”. Other kinds have matches.",
            fixture.ViewModel.ReviewKindEmptyText);
        Assert.True(fixture.ViewModel.HasReviewKindEmpty);
        Assert.False(fixture.ViewModel.HasReviewSearchEmpty);

        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(Category(fixture, ReviewCategoryKind.OtherMatches));
        Assert.False(fixture.ViewModel.HasReviewKindEmpty);

        // Nothing matches anywhere: the no-match line above the gallery says so instead.
        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(Category(fixture, ReviewCategoryKind.Exact));
        await fixture.SearchAsync("nothing");
        Assert.False(fixture.ViewModel.HasReviewKindEmpty);
        Assert.True(fixture.ViewModel.HasReviewSearchEmpty);

        await fixture.Search.ShowAllCommand.ExecuteAsync();

        Assert.Equal(string.Empty, fixture.ViewModel.ReviewKindEmptyText);
        Assert.Empty(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public async Task WhenSearchCannotRunReviewStaysUnfilteredAndTheBarSaysWhy()
    {
        var failed = new SearchFixture(index: SearchFixture.Ready with
        {
            State = SemanticState.Failed,
            Reason = "The photo search data could not be saved.",
        });
        Assert.Null(failed.Search.Filter);
        Assert.Equal(4, failed.ViewModel.ReviewCategories.Length);
        Assert.StartsWith("Search isn't available for this Scan:", failed.Search.PlaceholderText, StringComparison.Ordinal);

        // A search that can't run leaves Review unfiltered, even after one that filtered it.
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");
        await fixture.SearchAsync(new string('a', LikelyMatchesQuery.MaximumPromptLength + 1));

        Assert.Null(fixture.Search.Filter);
        Assert.Equal(4, fixture.ViewModel.ReviewCategories.Length);
        Assert.Equal(1, Category(fixture, ReviewCategoryKind.Exact).GroupCount);
        Assert.Equal("Use at most 60 characters. This one has 61.", fixture.Search.StatusText);
        Assert.True(fixture.Search.HasStatus);
    }

    [Fact]
    public async Task TheFilterAndTheRankingAreOneScoringPath()
    {
        var fixture = new SearchFixture();

        await fixture.SearchAsync("at sea");

        // One search is one ranking, and the photos labelled as matches are exactly the ranked ones.
        Assert.Single(fixture.Encoder.Prompts);
        var ranked = await new LikelyMatchesQuery(fixture.Encoder, fixture.Store)
            .RankAsync(fixture.Workflow.Current.Scan, "at sea", CancellationToken.None);
        var labelled = fixture.ViewModel.ReviewCategories
            .SelectMany(category => category.Groups).SelectMany(group => group.Members)
            .Where(member => member.MatchesSearch).Select(member => member.OccurrenceId).Distinct().Order();
        Assert.Equal(ranked.Matches.Select(match => match.OccurrenceId).Order(), labelled);
        var filter = Assert.IsType<ReviewSearchFilter>(fixture.Search.Filter);
        Assert.Equal(ranked.Matches.Length, filter.Matches.Count);
        Assert.All(ranked.Matches, match => Assert.Equal(match.Score, filter.Matches[match.OccurrenceId]));
    }

    [Fact]
    public async Task AfterACleanupTheFilterIsAppliedAgainToTheOpenSets()
    {
        var fixture = new SearchFixture();
        await fixture.SearchAsync("at sea");

        // The cleanup moved photo 1: it leaves every set, and its identical-copies set closes.
        fixture.Publish(group => group with
        {
            Closed = group.Candidate.GroupId == GroupedReviewData.ExactGroupId,
            Members = [.. group.Members.Where(member => member.Occurrence.Token.OccurrenceId != 1)],
            SelectedOccurrenceIds = group.SelectedOccurrenceIds.Remove(1),
        });

        Assert.Equal("at sea", fixture.Search.Filter?.Prompt);
        Assert.Empty(Category(fixture, ReviewCategoryKind.Exact).Groups);
        Assert.Equal([12L], Shown(fixture, ReviewCategoryKind.Near));
        Assert.Equal([21L, 22L], Shown(fixture, ReviewCategoryKind.Uncertain));
        Assert.Equal([50L], Shown(fixture, ReviewCategoryKind.OtherMatches));
        Assert.DoesNotContain(1L, fixture.ViewModel.ReviewCategories.SelectMany(category => ShownIds(category)));
        Assert.Empty(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public async Task TheWordsMakeNoCompletenessClassificationOrRemovalClaims()
    {
        var fixture = new SearchFixture();
        var texts = new List<string> { fixture.Search.StatusText, fixture.Search.ScopeText, ReviewSearchViewModel.PromptHelpText,
            fixture.Search.PlaceholderText };
        await fixture.SearchAsync("at sea");
        var other = Category(fixture, ReviewCategoryKind.OtherMatches);
        texts.AddRange([fixture.Search.StatusText, other.Description, other.CountText,
            Category(fixture, ReviewCategoryKind.Exact).CountText, ScanOccurrenceViewModel.NotInAnySetText]);
        await fixture.SearchAsync("lone");
        texts.AddRange([fixture.Search.StatusText, fixture.ViewModel.ApprovalHiddenMarkedText,
            fixture.ViewModel.ReviewSelectionBarText]);
        await fixture.SearchAsync("nothing");
        texts.Add(fixture.ViewModel.ReviewSearchEmptyText);

        Assert.All(texts, text =>
        {
            foreach (var banned in BannedWords)
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static readonly string[] BannedWords =
        ["vector", "embedding", "all matches", "every match", "category", "confidence", "will be removed", "delete"];

    private static ReviewCategoryViewModel Category(SearchFixture fixture, ReviewCategoryKind kind) =>
        fixture.ViewModel.ReviewCategories.Single(category => category.Category == kind);

    // The photos a kind shows, set by set in its order.
    private static long[] Shown(SearchFixture fixture, ReviewCategoryKind kind) => ShownIds(Category(fixture, kind));

    private static long[] ShownIds(ReviewCategoryViewModel category) =>
        [.. category.Groups.SelectMany(group => group.Members).Select(member => member.OccurrenceId)];

    private static ScanOccurrenceViewModel Member(SearchFixture fixture, ReviewCategoryKind kind, long occurrenceId) =>
        Assert.Single(Category(fixture, kind).Groups.SelectMany(group => group.Members),
            member => member.OccurrenceId == occurrenceId);
}

// GroupedReviewData's overlapping sets unless a test brings its own: photo 1 is marked in Identical
// copies and also in the Look-alike and Maybe-similar sets; photo 50 is in no set. The search bar's
// WPF tests use it too.
internal sealed class SearchFixture
{
    public static readonly EncoderProfile Profile = new(
        "siglip2-test", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 3, 1, 100, -10);

    public static readonly SemanticSnapshot Ready = new(SemanticState.Ready, 6, 6, 6, Profile.Fingerprint, "");

    public SearchFixture(SemanticSnapshot? index = null, Func<ShellSnapshot, AlbumenSnapshot>? snapshot = null,
        IReadOnlyDictionary<long, (float AtSea, float Lone)>? scores = null)
    {
        var scope = new ReviewStaticScope();
        var current = snapshot?.Invoke(scope.Current) ?? GroupedReviewData.OverlappingSnapshot(scope.Current);
        var source = current.Scan.Occurrences[0];
        var key = new PhysicalFileKey(7, new(0, 50));
        var lone = new ScanFileOccurrence(source.Token with { OccurrenceId = 50 }, @"C:\Photos\50.jpg",
            @"C:\Photos\50.jpg", key, source.Enumerate, source.Exact, new(key, 512, new string('A', 64)),
            source.Allocation);
        Workflow = new(current with
        {
            Scan = current.Scan with { Occurrences = current.Scan.Occurrences.Add(lone), Semantic = index ?? Ready },
        });
        Store = new(scores ?? PromptScores.Default);
        ViewModel = new ShellViewModel(scope, Workflow, likelyMatches: new LikelyMatchesQuery(Encoder, Store));
    }

    public TextEncoder Encoder { get; } = new();
    public PromptScores Store { get; }
    public PublishingReviewWorkflow Workflow { get; }
    public ShellViewModel ViewModel { get; }
    public ReviewSearchViewModel Search => ViewModel.ReviewSearch;

    public Task SearchAsync(string prompt)
    {
        Search.Prompt = prompt;
        return Search.SearchCommand.ExecuteAsync();
    }

    // Publishes Review with each set changed, as a cleanup would leave it.
    public void Publish(Func<ReviewGroupSnapshot, ReviewGroupSnapshot> change) =>
        Workflow.Publish(Workflow.Current with
        {
            Review = Workflow.Current.Review with { Groups = [.. Workflow.Current.Review.Groups.Select(change)] },
        });

    // The workflow's answer to marking a photo from that set: it is marked there.
    public void Mark(Guid groupId, long occurrenceId) => Publish(group => group.Candidate.GroupId != groupId ? group : group with
    {
        SelectedOccurrenceIds = group.SelectedOccurrenceIds.Add(occurrenceId),
        Members = [.. group.Members.Select(member => member.Occurrence.Token.OccurrenceId == occurrenceId
            ? member with { Selected = true } : member)],
    });

    // "at sea" encodes as (1, 0, 0) and "lone" as (0, 1, 0), so a photo's score for each is the
    // first or second component of its vector. Any other prompt scores nothing.
    internal sealed class TextEncoder : IVisionLanguageEncoder
    {
        public List<string> Prompts { get; } = [];

        public EncoderProfileResult LoadProfile() => new(Profile, null);

        public EncoderFailure? PrepareText(EncoderProfile profile) => null;

        public EmbeddingResult EncodeImage(EncoderProfile profile, EncoderImage image) => throw new NotSupportedException();

        public EmbeddingResult EncodeText(EncoderProfile profile, string prompt)
        {
            Prompts.Add(prompt);
            return new(prompt switch
            {
                "a photo of at sea" => new float[] { 1, 0, 0 },
                "a photo of lone" => [0, 1, 0],
                _ => [0, 0, 1],
            }, null);
        }
    }

    // Photos within ln 50 / 100 = 0.039 cosine of the best one are listed: by default "at sea" ranks
    // the grouped photos (2 falls far below) and photo 50; "lone" finds only 50.
    internal sealed class PromptScores(IReadOnlyDictionary<long, (float AtSea, float Lone)> scores) : IEmbeddingStore
    {
        public static readonly IReadOnlyDictionary<long, (float AtSea, float Lone)> Default =
            new Dictionary<long, (float AtSea, float Lone)>
            {
                [1] = (0.3f, 0), [2] = (0.01f, 0), [12] = (0.29f, 0), [21] = (0.28f, 0), [22] = (0.28f, 0), [50] = (0.27f, 0.5f),
            };

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
            CancellationToken cancellationToken)
        {
            foreach (var (id, (atSea, lone)) in scores)
                visit(new(id, new string('A', 64), [atSea, lone, -MathF.Sqrt(1 - atSea * atSea - lone * lone)]));
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class PublishingReviewWorkflow(AlbumenSnapshot current) : IAlbumenWorkflow
{
    public AlbumenSnapshot Current { get; private set; } = current;
    public List<SelectionCommand> SelectionCommands { get; } = [];
    public event EventHandler<AlbumenSnapshot>? SnapshotChanged;

    public void Publish(AlbumenSnapshot snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public Task ContinueAsync() => Task.CompletedTask;
    public Task SetScanOptionsAsync(ScanOptions options) => Task.CompletedTask;
    public Task ConfirmExactRulesAsync() => Task.CompletedTask;
    public Task StartScanAsync() => Task.CompletedTask;
    public Task CancelScanAsync() => Task.CompletedTask;
    public Task OpenReviewAsync() => Task.CompletedTask;
    public Task NavigateAsync(WorkflowPhase phase) => Task.CompletedTask;
    public Task ContinueReviewAsync() => Task.CompletedTask;
    public Task RevalidateUnknownAsync(Guid requestId, CancellationToken token) => Task.CompletedTask;

    public Task<ReviewCommandResult> ChangeReviewSelectionAsync(SelectionCommand command, CancellationToken token)
    {
        SelectionCommands.Add(command);
        return Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
    }

    public Task<ReviewCommandResult> AffirmReviewOverrideAsync(OverrideAffirmationCommand command, CancellationToken token) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));

    public Task<ReviewCommandResult> ApproveReviewAsync(ApproveSelectionCommand command, CancellationToken token) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
}
