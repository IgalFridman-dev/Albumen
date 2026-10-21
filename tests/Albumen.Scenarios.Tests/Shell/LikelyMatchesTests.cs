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

// E4-S4: the Likely matches list is a projection. It ranks, shows and opens photos, and never
// changes a set, a mark or a cleanup.
public sealed class LikelyMatchesTests
{
    [Theory]
    [InlineData(SemanticState.Idle, false, "Search isn't ready for this Scan. Turn on Look-alikes and scan again to search photos by what they show.")]
    [InlineData(SemanticState.Indexing, false, "Getting photos ready for search: 3 of 6 ready.")]
    [InlineData(SemanticState.Ready, true, "Describe what's in a photo, for example “at sea”, and press Enter.")]
    [InlineData(SemanticState.Empty, false, "No photo in this Scan could be prepared for search.")]
    [InlineData(SemanticState.Unavailable, false, "Search isn't installed: The model file text.onnx is not installed. Everything else in Review works.")]
    [InlineData(SemanticState.Failed, false, "Search isn't available for this Scan: The photo search data could not be saved. Everything else in Review works.")]
    public void EachSearchStateHasItsOwnWordsAndOnlyReadyCanSearch(SemanticState state, bool canSearch, string text)
    {
        var fixture = new Fixture(index: new(state, 6, 3, 3, Fixture.Profile.Fingerprint,
            state switch
            {
                SemanticState.Failed => "The photo search data could not be saved.",
                SemanticState.Unavailable => "The model file text.onnx is not installed.",
                _ => "",
            }));

        Assert.Equal(text, fixture.Matches.StatusText);
        Assert.Equal(canSearch, fixture.Matches.CanSearch);
        Assert.Equal(canSearch, fixture.Matches.SearchCommand.CanExecute(null));
        Assert.False(fixture.Matches.IsActive);
    }

    [Fact]
    public void ReadySaysItSearchesOnlyThisScanAndDisclosesPhotosThatCouldNotBePrepared()
    {
        Assert.Equal("Searches only the 6 photos in this Scan.", new Fixture().Matches.ScopeText);
        Assert.Equal("Searches only this Scan: 4 of 6 photos could be prepared, so some photos can't be found.",
            new Fixture(index: Fixture.Ready with { Indexed = 4 }).Matches.ScopeText);
    }

    [Fact]
    public async Task APromptListsTheScansPhotosByRankWithTheSetEachIsIn()
    {
        var fixture = new Fixture();

        await fixture.SearchAsync("  at   sea ");

        Assert.True(fixture.Matches.IsActive);
        Assert.Equal("5 likely matches in this Scan for “at sea”", fixture.Matches.StatusText);
        Assert.Equal([1L, 12L, 21L, 22L, 50L], Ids(fixture));
        Assert.Equal(["Identical copies set 1", "Look-alike set 1", "Maybe similar set 1", "Maybe similar set 1",
            "Not in any set: nothing to decide"], Items(fixture).Select(item => item.Context));
        Assert.False(Items(fixture)[4].CanOpen);
        Assert.Equal("Likely match 2 of 5: 12.jpg; Look-alike set 1", Items(fixture)[1].AutomationName);
    }

    [Fact]
    public async Task NoMatchSaysSoForThisScanOnly()
    {
        var fixture = new Fixture();

        await fixture.SearchAsync("nothing");

        Assert.True(fixture.Matches.IsActive);
        Assert.False(fixture.Matches.HasResults);
        Assert.Equal("No likely matches in this Scan for “nothing”. Try other words.", fixture.Matches.StatusText);
    }

    [Fact]
    public async Task ATooLongPromptIsRejectedAndNothingIsSearched()
    {
        var fixture = new Fixture();

        await fixture.SearchAsync(new string('a', LikelyMatchesQuery.MaximumPromptLength + 1));

        Assert.False(fixture.Matches.IsActive);
        Assert.Equal("Use at most 60 characters. This one has 61.", fixture.Matches.StatusText);
        Assert.Empty(fixture.Encoder.Prompts);
    }

    [Fact]
    public async Task MarksTheListDoesNotShowAreCountedAndKept()
    {
        var fixture = new Fixture();

        await fixture.SearchAsync("lone");

        Assert.Equal([50L], Ids(fixture));
        Assert.Equal("1 photo marked for removal isn't in this list. Cleanup still includes it.",
            fixture.Matches.SelectionOutsideText);
    }

    [Fact]
    public async Task OpeningAMatchShowsItInItsSetAndShowAllReturnsToWhereReviewWas()
    {
        var fixture = new Fixture();
        var review = fixture.Workflow.Current.Review;
        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(fixture.ViewModel.ReviewCategories[0]);
        await fixture.SearchAsync("at sea");

        await fixture.ViewModel.OpenLikelyMatchCommand.ExecuteAsync(Items(fixture)[1]);

        Assert.Equal(ReviewNavigationLevel.Comparisons, fixture.ViewModel.ReviewNavigationLevel);
        Assert.Equal(ReviewCategoryKind.Near, fixture.ViewModel.ActiveReviewCategory!.Category);
        Assert.Equal(12, fixture.ViewModel.ActiveReviewMember!.OccurrenceId);

        await fixture.Matches.ShowAllCommand.ExecuteAsync();

        Assert.False(fixture.Matches.IsActive);
        Assert.Equal(string.Empty, fixture.Matches.Prompt);
        Assert.Equal(ReviewNavigationLevel.Groups, fixture.ViewModel.ReviewNavigationLevel);
        Assert.Equal(ReviewCategoryKind.Exact, fixture.ViewModel.ActiveReviewCategory!.Category);
        Assert.Empty(fixture.Workflow.SelectionCommands);
        Assert.Same(review, fixture.Workflow.Current.Review);
    }

    [Fact]
    public async Task AnEmptyPromptClearsTheList()
    {
        var fixture = new Fixture();
        await fixture.SearchAsync("at sea");

        await fixture.SearchAsync("   ");

        Assert.False(fixture.Matches.IsActive);
        Assert.False(fixture.Matches.HasResults);
    }

    [Fact]
    public async Task AMarkChangedElsewhereKeepsTheListAndUpdatesTheCount()
    {
        var fixture = new Fixture();
        await fixture.SearchAsync("lone");
        var results = fixture.Matches.Results;

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Review = fixture.Workflow.Current.Review with
            {
                Groups = [.. fixture.Workflow.Current.Review.Groups.Select(group => group with
                {
                    Members = [.. group.Members.Select(member => member with { Selected = false, SelectedInGroup = null })],
                    SelectedOccurrenceIds = [],
                })],
            },
        });

        Assert.Same(results, fixture.Matches.Results);
        Assert.False(fixture.Matches.HasSelectionOutside);
    }

    [Fact]
    public async Task PhotosThatLeftReviewAreNotListed()
    {
        var fixture = new Fixture();
        await fixture.SearchAsync("at sea");

        // After a cleanup the moved photo leaves every set but stays in the Scan.
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Review = fixture.Workflow.Current.Review with
            {
                Groups = [.. fixture.Workflow.Current.Review.Groups.Select(group => group with
                {
                    Members = [.. group.Members.Where(member => member.Occurrence.Token.OccurrenceId != 12)],
                })],
            },
        });

        Assert.Equal([1L, 21L, 22L, 50L], Ids(fixture));
    }

    [Fact]
    public async Task ANewScanClearsThePromptAndTheList()
    {
        var fixture = new Fixture();
        fixture.Matches.Prompt = "at sea";
        await fixture.Matches.SearchCommand.ExecuteAsync();

        var scan = fixture.Workflow.Current.Scan;
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = scan with { Pin = scan.Pin! with { ScanId = Guid.NewGuid() } },
        });

        Assert.Equal(string.Empty, fixture.Matches.Prompt);
        Assert.False(fixture.Matches.IsActive);
    }

    [Fact]
    public async Task TheWordsMakeNoCompletenessClassificationOrRemovalClaims()
    {
        var fixture = new Fixture();
        var texts = new List<string> { fixture.Matches.StatusText, fixture.Matches.ScopeText, LikelyMatchesViewModel.PromptHelpText };
        await fixture.SearchAsync("at sea");
        texts.AddRange([fixture.Matches.StatusText, .. Items(fixture).Select(item => item.Context)]);
        await fixture.SearchAsync("lone");
        texts.AddRange([fixture.Matches.StatusText, fixture.Matches.SelectionOutsideText]);

        Assert.All(texts, text =>
        {
            foreach (var banned in BannedWords)
                Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static readonly string[] BannedWords =
        ["vector", "embedding", "all matches", "every match", "category", "confidence", "will be removed", "delete"];

    private static ImmutableArray<LikelyMatchItemViewModel> Items(Fixture fixture) =>
        [.. Enumerable.Range(0, fixture.Matches.Results.Count).Select(index => fixture.Matches.Results[index])];

    private static IEnumerable<long> Ids(Fixture fixture) => Items(fixture).Select(item => item.Photo.OccurrenceId);

    // GroupedReviewData's overlapping sets: photo 1 is marked in Identical copies and also in the
    // Look-alike and Maybe-similar sets; photo 50 is in no set.
    private sealed class Fixture
    {
        public static readonly EncoderProfile Profile = new(
            "siglip2-test", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 3, 1, 100, -10);

        public static readonly SemanticSnapshot Ready = new(SemanticState.Ready, 6, 6, 6, Profile.Fingerprint, "");

        public Fixture(SemanticSnapshot? index = null)
        {
            var scope = new ReviewStaticScope();
            var snapshot = GroupedReviewData.OverlappingSnapshot(scope.Current);
            var source = snapshot.Scan.Occurrences[0];
            var key = new PhysicalFileKey(7, new(0, 50));
            var lone = new ScanFileOccurrence(source.Token with { OccurrenceId = 50 }, @"C:\Photos\50.jpg",
                @"C:\Photos\50.jpg", key, source.Enumerate, source.Exact, new(key, 512, new string('A', 64)),
                source.Allocation);
            Workflow = new(snapshot with
            {
                Scan = snapshot.Scan with { Occurrences = snapshot.Scan.Occurrences.Add(lone), Semantic = index ?? Ready },
            });
            ViewModel = new ShellViewModel(scope, Workflow, likelyMatches: new LikelyMatchesQuery(Encoder, Store));
        }

        public TextEncoder Encoder { get; } = new();
        public PromptScores Store { get; } = new();
        public PublishingReviewWorkflow Workflow { get; }
        public ShellViewModel ViewModel { get; }
        public LikelyMatchesViewModel Matches => ViewModel.LikelyMatches;

        public Task SearchAsync(string prompt)
        {
            Matches.Prompt = prompt;
            return Matches.SearchCommand.ExecuteAsync();
        }
    }

    // "at sea" encodes as (1, 0, 0) and "lone" as (0, 1, 0), so a photo's score for each is the
    // first or second component of its vector. Any other prompt scores nothing.
    private sealed class TextEncoder : IVisionLanguageEncoder
    {
        public List<string> Prompts { get; } = [];

        public EncoderProfileResult LoadProfile() => new(Fixture.Profile, null);

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

    // With scale 100 and bias -10 a photo matches at cosine 0.1 and up: "at sea" ranks the grouped
    // photos (2 falls below) and photo 50; "lone" finds only 50.
    private sealed class PromptScores : IEmbeddingStore
    {
        private static readonly Dictionary<long, (float AtSea, float Lone)> Scores = new()
        {
            [1] = (0.3f, 0), [2] = (0.01f, 0), [12] = (0.25f, 0), [21] = (0.2f, 0), [22] = (0.2f, 0), [50] = (0.15f, 0.5f),
        };

        public ValueTask ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AddAsync(Guid scanId, EncoderProfile profile, IReadOnlyList<StoredEmbedding> embeddings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ReadAsync(Guid scanId, EncoderProfile profile, Action<StoredEmbedding> visit,
            CancellationToken cancellationToken)
        {
            foreach (var (id, (atSea, lone)) in Scores)
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
