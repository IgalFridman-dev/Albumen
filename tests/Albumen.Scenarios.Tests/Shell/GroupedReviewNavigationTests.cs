using System.Collections.Immutable;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Shell;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class GroupedReviewNavigationTests
{
    [Fact]
    public async Task ReviewAlwaysExposesTheFourStableCategoriesInEvidenceOrder()
    {
        var fixture = GroupedReviewFixture.Create();

        Assert.Collection(fixture.ViewModel.ReviewCategories,
            row => AssertCategory(row, ReviewCategoryKind.Exact, "Identical copies", 1),
            row => AssertCategory(row, ReviewCategoryKind.Near, "Look-alike", 1),
            row => AssertCategory(row, ReviewCategoryKind.Uncertain, "Maybe similar", 1),
            row => AssertCategory(row, ReviewCategoryKind.CouldNotAnalyze, "Couldn't check", 1));
        Assert.Equal([1, 2, 3, 4], fixture.ViewModel.ReviewCategories.Select(row => row.PositionInSet));
        Assert.All(fixture.ViewModel.ReviewCategories, row => Assert.Equal(4, row.SizeOfSet));

        // While a search filters Review, Other matches comes fifth, before Couldn't check.
        var search = new SearchFixture();
        await search.SearchAsync("at sea");

        Assert.Equal([ReviewCategoryKind.Exact, ReviewCategoryKind.Near, ReviewCategoryKind.Uncertain,
                ReviewCategoryKind.OtherMatches, ReviewCategoryKind.CouldNotAnalyze],
            search.ViewModel.ReviewCategories.Select(row => row.Category));
        Assert.Equal([1, 2, 3, 4, 5], search.ViewModel.ReviewCategories.Select(row => row.PositionInSet));
        Assert.All(search.ViewModel.ReviewCategories, row => Assert.Equal(5, row.SizeOfSet));

        await search.Search.ShowAllCommand.ExecuteAsync();

        Assert.Equal(4, search.ViewModel.ReviewCategories.Length);
    }

    [Fact]
    public async Task CategoryAndBackNavigationOwnOnlyViewState()
    {
        var fixture = GroupedReviewFixture.Create();
        var sourceScan = fixture.Workflow.Current.Scan;
        var sourceReview = fixture.Workflow.Current.Review;
        var near = fixture.ViewModel.ReviewCategories[1];

        Assert.Equal(ReviewNavigationLevel.Categories, fixture.ViewModel.ReviewNavigationLevel);
        Assert.Equal(["Review"], fixture.ViewModel.ReviewBreadcrumbs);
        Assert.Null(fixture.ViewModel.ActiveReviewGroups);
        Assert.False(fixture.ViewModel.ReviewBackCommand.CanExecute(null));

        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(near);

        // Opening a kind shows all of its sets: there is no set or photo level below it.
        Assert.Equal(ReviewNavigationLevel.Gallery, fixture.ViewModel.ReviewNavigationLevel);
        Assert.Same(near, fixture.ViewModel.ActiveReviewCategory);
        Assert.Same(near.Groups, fixture.ViewModel.ActiveReviewGroups);
        Assert.Equal(["Review", "Look-alike"], fixture.ViewModel.ReviewBreadcrumbs);
        Assert.True(fixture.ViewModel.ReviewBackCommand.CanExecute(null));
        var group = near.Groups[0];
        Assert.StartsWith("Set 1: ", group.Summary, StringComparison.Ordinal);

        var member = group.Members[1];
        await fixture.ViewModel.PlaceInViewerCommand.ExecuteAsync(
            new PlaceInViewer(ViewerSide.Left, group.GroupId, member));

        Assert.Same(member, fixture.ViewModel.LeftViewer.Member);
        Assert.Equal(ReviewNavigationLevel.Gallery, fixture.ViewModel.ReviewNavigationLevel);

        await fixture.ViewModel.ReviewBackCommand.ExecuteAsync();

        Assert.Equal(ReviewNavigationLevel.Categories, fixture.ViewModel.ReviewNavigationLevel);
        Assert.Null(fixture.ViewModel.ActiveReviewCategory);
        Assert.Null(fixture.ViewModel.ActiveReviewGroups);
        Assert.Equal(["Review"], fixture.ViewModel.ReviewBreadcrumbs);
        // The viewers are not a navigation level: going back leaves the photo shown.
        Assert.Same(member, fixture.ViewModel.LeftViewer.Member);
        Assert.Same(sourceScan, fixture.Workflow.Current.Scan);
        Assert.Same(sourceReview, fixture.Workflow.Current.Review);
        Assert.Empty(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public async Task AProjectionKeepsTheOpenKindOpenWithItsNewSets()
    {
        var scope = new ReviewStaticScope();
        var workflow = new RecordingReviewWorkflow(scope.Current, ReviewData.CreateSnapshot(twoGroups: true));
        await using var viewModel = new ShellViewModel(scope, workflow);
        var exact = viewModel.ReviewCategories[0];
        await viewModel.OpenReviewCategoryCommand.ExecuteAsync(exact);

        workflow.Publish(ReviewData.WithSelection(workflow.Current.Review, [1], 10));

        Assert.Equal(ReviewNavigationLevel.Gallery, viewModel.ReviewNavigationLevel);
        Assert.NotSame(exact, viewModel.ActiveReviewCategory);
        Assert.Equal(ReviewCategoryKind.Exact, viewModel.ActiveReviewCategory?.Category);
        Assert.Same(viewModel.ReviewCategories[0].Groups, viewModel.ActiveReviewGroups);
        Assert.True(viewModel.ActiveReviewGroups?[0].Members[0].IsSelected);
        Assert.Equal(["Review", "Identical copies"], viewModel.ReviewBreadcrumbs);
    }

    [Fact]
    public async Task AMarkHeldInAnotherSetShowsAsMarkedHereAndSettlesThePair()
    {
        var fixture = GroupedReviewFixture.CreateOverlapping();
        var exact = fixture.ViewModel.ReviewCategories[0].Groups[0];
        var near = fixture.ViewModel.ReviewCategories[1].Groups[0];
        var uncertain = fixture.ViewModel.ReviewCategories[2].Groups[0];
        var markedHere = exact.Members[0];
        var markedElsewhere = near.Members[0];

        Assert.Equal("Marked for removal", markedHere.EligibilityText);
        Assert.True(markedHere.CanSelect);
        Assert.False(markedHere.IsMarkedElsewhere);
        Assert.Equal("1 photo marked for removal", exact.SelectionStatus);
        Assert.False(exact.IsSettledElsewhere);

        Assert.Equal("Marked for removal in Identical copies set 1", markedElsewhere.EligibilityText);
        Assert.True(markedElsewhere.IsSelected);
        Assert.True(markedElsewhere.IsMarkedElsewhere);
        Assert.False(markedElsewhere.CanSelect);
        Assert.False(markedElsewhere.ToggleSelectionCommand!.CanExecute(null));
        Assert.StartsWith("Marked for removal: ", markedElsewhere.AutomationName, StringComparison.Ordinal);
        Assert.StartsWith("Marked for removal in Identical copies set 1;", markedElsewhere.ItemStatus,
            StringComparison.Ordinal);
        Assert.Equal("Kept", near.Members[1].EligibilityText);
        Assert.True(near.Members[1].CanSelect);
        Assert.Equal("Nothing left to decide: 1 marked elsewhere", near.SelectionStatus);
        Assert.True(near.IsSettledElsewhere);
        Assert.False(near.SelectAllCommand.CanExecute(null));

        Assert.Equal("Nothing marked yet; 1 marked elsewhere", uncertain.SelectionStatus);
        Assert.False(uncertain.IsSettledElsewhere);
        Assert.True(uncertain.SelectAllCommand.CanExecute(null));
        await uncertain.SelectAllCommand.ExecuteAsync();
        var selectAll = Assert.Single(fixture.Workflow.SelectionCommands);
        Assert.Equal([21L, 22L], selectAll.OccurrenceIds);

        await fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(fixture.ViewModel.ReviewCategories[1]);
        await fixture.ViewModel.PlaceInViewerCommand.ExecuteAsync(
            new PlaceInViewer(ViewerSide.Left, near.GroupId, markedElsewhere));

        Assert.Same(markedElsewhere, fixture.ViewModel.LeftViewer.Member);
        Assert.True(fixture.ViewModel.LeftViewer.IsMarked);
        Assert.False(fixture.ViewModel.LeftViewer.CanRemove);
        Assert.Single(fixture.Workflow.SelectionCommands);
    }

    [Fact]
    public void CandidateGroupsKeepTheirExactNearAndUncertainClassification()
    {
        var fixture = GroupedReviewFixture.Create();

        Assert.Equal(GroupedReviewFixture.ExactGroupId,
            Assert.Single(fixture.ViewModel.ReviewCategories[0].Groups).GroupId);
        Assert.Equal(GroupedReviewFixture.NearGroupId,
            Assert.Single(fixture.ViewModel.ReviewCategories[1].Groups).GroupId);
        Assert.Equal(GroupedReviewFixture.UncertainGroupId,
            Assert.Single(fixture.ViewModel.ReviewCategories[2].Groups).GroupId);
        Assert.Equal(ReviewCategoryKind.Exact, fixture.ViewModel.ReviewCategories[0].Groups[0].Category);
        Assert.Equal(ReviewCategoryKind.Near, fixture.ViewModel.ReviewCategories[1].Groups[0].Category);
        Assert.Equal(ReviewCategoryKind.Uncertain, fixture.ViewModel.ReviewCategories[2].Groups[0].Category);
    }

    [Fact]
    public void EachCouldNotAnalyzeOccurrenceIsAStableSingletonGroup()
    {
        var fixture = GroupedReviewFixture.Create(includeSecondFailure: true);
        var category = fixture.ViewModel.ReviewCategories[3];

        Assert.Equal(2, category.GroupCount);
        Assert.Equal(2, category.Groups.Count);
        Assert.Collection(category.Groups,
            group => Assert.Equal([GroupedReviewFixture.FailedOccurrenceId],
                group.Members.Select(member => member.OccurrenceId)),
            group => Assert.Equal([GroupedReviewFixture.SecondFailedOccurrenceId],
                group.Members.Select(member => member.OccurrenceId)));
        Assert.All(category.Groups, group =>
        {
            Assert.Equal(ReviewCategoryKind.CouldNotAnalyze, group.Category);
            Assert.Equal(1, group.MemberCount);
        });
    }

    [Fact]
    public void NearFeatureFailureIsRoutedToCouldNotAnalyzeAfterExactSucceeded()
    {
        var fixture = new GroupedReviewFixture(
            new GroupedReviewWorkflow(GroupedReviewData.NearFeatureFailureSnapshot));

        var category = fixture.ViewModel.ReviewCategories.Single(
            row => row.Category == ReviewCategoryKind.CouldNotAnalyze);
        var baseline = GroupedReviewFixture.Create().ViewModel.ReviewCategories.Single(
            row => row.Category == ReviewCategoryKind.CouldNotAnalyze).GroupCount;
        Assert.Equal(baseline + 1, category.GroupCount);
        var group = category.Groups.Single(candidate =>
            candidate.Members.Any(member => member.OccurrenceId == 11));
        var member = Assert.Single(group.Members);

        Assert.Equal(11, member.OccurrenceId);
        Assert.Equal(ReviewCategoryKind.CouldNotAnalyze, group.Category);
        Assert.Empty(fixture.ViewModel.ReviewCategories.Single(
            row => row.Category == ReviewCategoryKind.Near).Groups);
    }

    [Fact]
    public void TenThousandRowsMaterializeOnlyRequestedIndicesAndMemoizeThem()
    {
        var fixture = GroupedReviewFixture.CreateLarge(10_000);
        var collection = fixture.ViewModel.ReviewCategories[0].Groups;

        Assert.Equal(10_000, collection.Count);
        Assert.Equal(0, collection.MaterializedCount);

        var first = collection[0];
        var middle = collection[5_000];
        var last = collection[9_999];
        Assert.Same(middle, collection[5_000]);

        Assert.Equal(3, collection.MaterializedCount);
        Assert.Equal(0, first.Members.MaterializedCount);
        Assert.Equal(0, middle.Members.MaterializedCount);
        Assert.Equal(0, last.Members.MaterializedCount);

        var middleFirstMember = middle.Members[0];
        var middleSecondMember = middle.Members[1];
        Assert.Same(middleFirstMember, middle.Members[0]);

        Assert.NotSame(middleFirstMember, middleSecondMember);
        Assert.Equal(2, middle.Members.MaterializedCount);
    }

    private static void AssertCategory(
        ReviewCategoryViewModel row,
        ReviewCategoryKind category,
        string name,
        int groupCount)
    {
        Assert.Equal(category, row.Category);
        Assert.Equal(name, row.Name);
        Assert.Equal(groupCount, row.GroupCount);
    }
}

internal sealed class GroupedReviewFixture
{
    internal GroupedReviewFixture(GroupedReviewWorkflow workflow)
    {
        Workflow = workflow;
        ViewModel = new ShellViewModel(workflow.Scope, workflow);
    }

    public static Guid ExactGroupId => GroupedReviewData.ExactGroupId;
    public static Guid NearGroupId => GroupedReviewData.NearGroupId;
    public static Guid UncertainGroupId => GroupedReviewData.UncertainGroupId;
    public static long FailedOccurrenceId => GroupedReviewData.FailedOccurrenceId;
    public static long SecondFailedOccurrenceId => GroupedReviewData.SecondFailedOccurrenceId;
    public GroupedReviewWorkflow Workflow { get; }
    public ShellViewModel ViewModel { get; }

    public static GroupedReviewFixture Create(bool includeSecondFailure = false) =>
        new(new GroupedReviewWorkflow(includeSecondFailure));

    public static GroupedReviewFixture CreateLarge(int groupCount) =>
        new(new GroupedReviewWorkflow(GroupedReviewData.LargeSnapshot(groupCount)));

    public static GroupedReviewFixture CreateOverlapping() =>
        new(new GroupedReviewWorkflow(GroupedReviewData.OverlappingSnapshot));
}

internal sealed class GroupedReviewWorkflow : IAlbumenWorkflow
{
    public GroupedReviewWorkflow(bool includeSecondFailure)
    {
        Scope = new ReviewStaticScope();
        Current = GroupedReviewData.Snapshot(Scope.Current, includeSecondFailure);
    }

    public GroupedReviewWorkflow(Func<ShellSnapshot, AlbumenSnapshot> snapshotFactory)
    {
        Scope = new ReviewStaticScope();
        Current = snapshotFactory(Scope.Current);
    }

    public ReviewStaticScope Scope { get; }
    public AlbumenSnapshot Current { get; }
    public List<SelectionCommand> SelectionCommands { get; } = [];
    public event EventHandler<AlbumenSnapshot>? SnapshotChanged { add { } remove { } }
    public Task ContinueAsync() => Task.CompletedTask;
    public Task SetScanOptionsAsync(ScanOptions options) => Task.CompletedTask;
    public Task ConfirmExactRulesAsync() => Task.CompletedTask;
    public Task StartScanAsync() => Task.CompletedTask;
    public Task CancelScanAsync() => Task.CompletedTask;
    public Task OpenReviewAsync() => Task.CompletedTask;
    public Task NavigateAsync(WorkflowPhase phase) => Task.CompletedTask;
    public Task ContinueReviewAsync() => Task.CompletedTask;
    public Task RevalidateUnknownAsync(Guid requestId, CancellationToken token) => Task.CompletedTask;

    public Task<ReviewCommandResult> ChangeReviewSelectionAsync(
        SelectionCommand command,
        CancellationToken token)
    {
        SelectionCommands.Add(command);
        return Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
    }

    public Task<ReviewCommandResult> AffirmReviewOverrideAsync(
        OverrideAffirmationCommand command,
        CancellationToken token) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));

    public Task<ReviewCommandResult> ApproveReviewAsync(
        ApproveSelectionCommand command,
        CancellationToken token) =>
        Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
}

internal static class GroupedReviewData
{
    internal static readonly Guid ExactGroupId = Guid.Parse("31000000-0000-0000-0000-000000000001");
    internal static readonly Guid NearGroupId = Guid.Parse("32000000-0000-0000-0000-000000000002");
    internal static readonly Guid UncertainGroupId = Guid.Parse("33000000-0000-0000-0000-000000000003");
    internal const long FailedOccurrenceId = 41;
    internal const long SecondFailedOccurrenceId = 42;

    internal static AlbumenSnapshot Snapshot(ShellSnapshot scope, bool includeSecondFailure)
    {
        var pin = new ScanPin(Guid.Parse("11000000-0000-0000-0000-000000000001"), scope.Revision, 3, 5);
        var exact = Group(pin, ExactGroupId, CandidateCategory.Exact, false, 1, 2);
        var near = Group(pin, NearGroupId, CandidateCategory.NearDuplicate, false, 11, 12);
        var uncertain = Group(pin, UncertainGroupId, CandidateCategory.Uncertain, true, 21, 22);
        var failures = ImmutableArray.Create(Failed(pin, FailedOccurrenceId, ScanReason.AccessDenied));
        if (includeSecondFailure)
            failures = failures.Add(Failed(pin, SecondFailedOccurrenceId, ScanReason.CodecUnavailable));
        var groups = ImmutableArray.Create(exact.Candidate, near.Candidate, uncertain.Candidate);
        var occurrences = groups.SelectMany(group => group.Members).Concat(failures).ToImmutableArray();
        var scan = new ScanSnapshot(pin, ScanStatus.Incomplete,
            new(ScanStageState.Succeeded, ScanReason.None), occurrences, groups,
            new(occurrences.Length, occurrences.Length - failures.Length, 0, failures.Length, 0, 0));
        var review = new ReviewSnapshot(
            new(Guid.Parse("21000000-0000-0000-0000-000000000001"), pin),
            ReviewStatus.Ready,
            ScanStatus.Incomplete,
            9,
            [exact, near, uncertain],
            new(0, failures.Length, 0, 0, 0, 0, 0),
            false,
            null,
            ReviewUnavailableReason.None);
        return new AlbumenSnapshot(scope, new(scope.Revision, 3), scan, WorkflowPhase.Review,
            InitialWorkflow.Create()) { Review = review };
    }

    internal static Func<ShellSnapshot, AlbumenSnapshot> LargeSnapshot(int groupCount) => scope =>
    {
        var pin = new ScanPin(Guid.Parse("12000000-0000-0000-0000-000000000001"), scope.Revision, 3, 5);
        var reviewGroups = Enumerable.Range(0, groupCount)
            .Select(index => Group(pin, GroupId(index), CandidateCategory.Exact, false,
                checked(index * 2L + 1), checked(index * 2L + 2)))
            .ToImmutableArray();
        var candidates = reviewGroups.Select(group => group.Candidate).ToImmutableArray();
        var occurrences = candidates.SelectMany(group => group.Members).ToImmutableArray();
        var scan = new ScanSnapshot(pin, ScanStatus.Complete,
            new(ScanStageState.Succeeded, ScanReason.None), occurrences, candidates,
            new(occurrences.Length, occurrences.Length, 0, 0, 0, 0));
        var review = new ReviewSnapshot(
            new(Guid.Parse("22000000-0000-0000-0000-000000000001"), pin),
            ReviewStatus.Ready,
            ScanStatus.Complete,
            1,
            reviewGroups,
            new(0, 0, 0, 0, 0, 0, 0),
            false,
            null,
            ReviewUnavailableReason.None);
        return new AlbumenSnapshot(scope, new(scope.Revision, 3), scan, WorkflowPhase.Review,
            InitialWorkflow.Create()) { Review = review };
    };

    // Photo 1 is in the Identical-copies set (marked there), in the Look-alike pair and in
    // a Maybe-similar set of three. The one mark is projected into the other two sets.
    internal static AlbumenSnapshot OverlappingSnapshot(ShellSnapshot scope)
    {
        var pin = new ScanPin(Guid.Parse("13000000-0000-0000-0000-000000000001"), scope.Revision, 3, 5);
        var shared = Analyzed(pin, 1);
        var exact = Group(pin, ExactGroupId, CandidateCategory.Exact, false,
            [Member(shared, selected: true), Member(Analyzed(pin, 2))], selected: [1]);
        var near = Group(pin, NearGroupId, CandidateCategory.NearDuplicate, false,
            [Member(shared, selectedIn: ExactGroupId), Member(Analyzed(pin, 12))]);
        var uncertain = Group(pin, UncertainGroupId, CandidateCategory.Uncertain, true,
            [Member(shared, selectedIn: ExactGroupId), Member(Analyzed(pin, 21)), Member(Analyzed(pin, 22))]);
        var groups = ImmutableArray.Create(exact.Candidate, near.Candidate, uncertain.Candidate);
        var occurrences = groups.SelectMany(group => group.Members).DistinctBy(row => row.Token.OccurrenceId)
            .ToImmutableArray();
        var scan = new ScanSnapshot(pin, ScanStatus.Complete,
            new(ScanStageState.Succeeded, ScanReason.None), occurrences, groups,
            new(occurrences.Length, occurrences.Length, 0, 0, 0, 0));
        var review = new ReviewSnapshot(
            new(Guid.Parse("23000000-0000-0000-0000-000000000001"), pin),
            ReviewStatus.Ready,
            ScanStatus.Complete,
            2,
            [exact, near, uncertain],
            new(0, 0, 0, 0, 0, 0, 0),
            false,
            null,
            ReviewUnavailableReason.None);
        return new AlbumenSnapshot(scope, new(scope.Revision, 3), scan, WorkflowPhase.Review,
            InitialWorkflow.Create()) { Review = review };
    }

    private static ReviewMemberSnapshot Member(ScanFileOccurrence occurrence, bool selected = false,
        Guid? selectedIn = null) =>
        new(occurrence, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None, selected, selectedIn);

    private static ReviewGroupSnapshot Group(
        ScanPin pin,
        Guid groupId,
        CandidateCategory category,
        bool uncertain,
        ImmutableArray<ReviewMemberSnapshot> members,
        ImmutableArray<long> selected = default)
    {
        var candidate = new CandidateGroup(groupId, 4, pin, category,
            members.Select(member => member.Occurrence).ToImmutableArray(), [], uncertain,
            null, new(1024, RecoveryUnavailableReason.None));
        return new ReviewGroupSnapshot(candidate, members,
            selected.IsDefault ? ImmutableHashSet<long>.Empty : selected.ToImmutableHashSet(),
            new(0, SelectionRecoveryUnavailableReason.None), false, null);
    }

    internal static AlbumenSnapshot NearFeatureFailureSnapshot(ShellSnapshot scope)
    {
        var snapshot = Snapshot(scope, includeSecondFailure: false);
        var failed = snapshot.Scan.Occurrences.Single(row => row.Token.OccurrenceId == 11) with
        {
            VisualFeatures = new(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            NearDuplicate = new(ScanStageState.NotApplicable, ScanReason.ReadFailure),
        };
        var occurrences = snapshot.Scan.Occurrences
            .Select(row => row.Token.OccurrenceId == failed.Token.OccurrenceId ? failed : row)
            .ToImmutableArray();
        var scanGroups = snapshot.Scan.Groups
            .Where(group => group.Category != CandidateCategory.NearDuplicate)
            .ToImmutableArray();
        var reviewGroups = snapshot.Review.Groups
            .Where(group => group.Candidate.Category != CandidateCategory.NearDuplicate)
            .ToImmutableArray();
        return snapshot with
        {
            Scan = snapshot.Scan with { Occurrences = occurrences, Groups = scanGroups },
            Review = snapshot.Review with { Groups = reviewGroups },
        };
    }

    private static Guid GroupId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 1;
        return new Guid(bytes);
    }

    private static ReviewGroupSnapshot Group(
        ScanPin pin,
        Guid groupId,
        CandidateCategory category,
        bool uncertain,
        long firstId,
        long secondId)
    {
        var first = Analyzed(pin, firstId);
        var second = Analyzed(pin, secondId);
        var candidate = new CandidateGroup(groupId, 4, pin, category, [first, second], [], uncertain,
            null, new(1024, RecoveryUnavailableReason.None));
        return new ReviewGroupSnapshot(candidate,
            [
                new(first, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None, false),
                new(second, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None, false),
            ],
            [],
            new(0, SelectionRecoveryUnavailableReason.None),
            false,
            null);
    }

    private static ScanFileOccurrence Analyzed(ScanPin pin, long id)
    {
        var key = new PhysicalFileKey(7, new(0, checked((ulong)id)));
        var path = $@"C:\Photos\{id}.jpg";
        return new(new(pin, id, 2, 3), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 512, new string('A', 64)),
            new(512, 1, true));
    }

    private static ScanFileOccurrence Failed(ScanPin pin, long id, ScanReason reason)
    {
        var state = reason == ScanReason.CodecUnavailable
            ? ScanStageState.Unsupported
            : ScanStageState.FailedRetryable;
        var path = $@"C:\Photos\failed-{id}.jpg";
        return new(new(pin, id, 2, 3), path, path, null,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(state, reason),
            null,
            new(null, null, false));
    }
}
