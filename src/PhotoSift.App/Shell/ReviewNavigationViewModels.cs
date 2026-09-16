using System.Collections.Immutable;
using System.Globalization;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.App.Shell;

public enum ReviewCategoryKind
{
    Exact,
    Near,
    Uncertain,
    CouldNotAnalyze,
}

public enum ReviewNavigationLevel
{
    Categories,
    Groups,
    Comparisons,
}

public sealed class ReviewCategoryViewModel
{
    private readonly Func<Guid, int?> findGroupIndex;

    internal ReviewCategoryViewModel(
        ReviewCategoryKind category,
        string name,
        int groupCount,
        int positionInSet,
        int sizeOfSet,
        Func<int, ReviewGroupViewModel> groupFactory,
        Func<Guid, int?> findGroupIndex)
    {
        Category = category;
        Name = name;
        GroupCount = groupCount;
        PositionInSet = positionInSet;
        SizeOfSet = sizeOfSet;
        Groups = new(groupCount, groupFactory);
        this.findGroupIndex = findGroupIndex;
    }

    public ReviewCategoryKind Category { get; }
    public string Name { get; }
    public int GroupCount { get; }
    public int PositionInSet { get; }
    public int SizeOfSet { get; }
    public string AutomationName => $"{Name}: {GroupCount.ToString(CultureInfo.InvariantCulture)} groups";
    public LazyReviewCollection<ReviewGroupViewModel> Groups { get; }

    internal ReviewGroupViewModel? FindGroup(Guid groupId) =>
        findGroupIndex(groupId) is { } index ? Groups[index] : null;
}

public sealed class ReviewGroupViewModel
{
    private readonly Dictionary<long, int> memberIndexes;

    internal ReviewGroupViewModel(
        CandidateGroup group,
        ReviewCategoryKind category,
        int positionInSet,
        int sizeOfSet)
    {
        GroupId = group.GroupId;
        Category = category;
        PositionInSet = positionInSet;
        SizeOfSet = sizeOfSet;
        var members = group.Members.OrderBy(member => member.Token.OccurrenceId).ToImmutableArray();
        memberIndexes = members
            .Select((member, index) => (OccurrenceId: member.Token.OccurrenceId, Index: index))
            .ToDictionary(pair => pair.OccurrenceId, pair => pair.Index);
        Members = new(members.Length, index => new ScanOccurrenceViewModel(
            members[index], IsAlias(members, index)));
        MemberCount = members.Length;
        Summary = FormatSummary(category, MemberCount);
        SelectAllCommand = DisabledCommand();
        ReplaceSelectionCommand = DisabledCommand(parameterized: true);
    }

    internal ReviewGroupViewModel(
        ReviewGroupSnapshot group,
        ReviewCategoryKind category,
        int positionInSet,
        int sizeOfSet,
        bool reviewAllowsSelection,
        Func<SelectionCommandKind, ImmutableArray<long>, Task> changeSelection)
    {
        GroupId = group.Candidate.GroupId;
        Category = category;
        PositionInSet = positionInSet;
        SizeOfSet = sizeOfSet;
        var members = group.Members.OrderBy(member => member.Occurrence.Token.OccurrenceId).ToImmutableArray();
        memberIndexes = members
            .Select((member, index) => (OccurrenceId: member.Occurrence.Token.OccurrenceId, Index: index))
            .ToDictionary(pair => pair.OccurrenceId, pair => pair.Index);
        var canChangeSelection = reviewAllowsSelection && !group.Sealed;
        Members = new(members.Length, index => new ScanOccurrenceViewModel(
            members[index], IsAlias(members, index), canChangeSelection,
            () => changeSelection(SelectionCommandKind.Toggle,
                ImmutableArray.Create(members[index].Occurrence.Token.OccurrenceId))));
        MemberCount = members.Length;
        Summary = FormatSummary(category, MemberCount);
        var selectable = members
            .Where(member => member.Eligibility == ReviewEligibilityState.Eligible)
            .Select(member => member.Occurrence.Token.OccurrenceId)
            .Order()
            .ToImmutableArray();
        SelectAllCommand = new AsyncCommand(
            () => changeSelection(SelectionCommandKind.ReplaceFocusedGroup, selectable),
            canExecute: () => canChangeSelection && !selectable.IsEmpty);
        ReplaceSelectionCommand = new AsyncCommand(
            parameter => changeSelection(SelectionCommandKind.ReplaceFocusedGroup, RequireIds(parameter)),
            canExecute: () => canChangeSelection);
    }

    internal ReviewGroupViewModel(ScanFileOccurrence occurrence, int positionInSet, int sizeOfSet)
    {
        GroupId = Guid.Empty;
        Category = ReviewCategoryKind.CouldNotAnalyze;
        PositionInSet = positionInSet;
        SizeOfSet = sizeOfSet;
        memberIndexes = new Dictionary<long, int> { [occurrence.Token.OccurrenceId] = 0 };
        Members = new(1, _ => new ScanOccurrenceViewModel(occurrence));
        MemberCount = 1;
        Summary = $"Could Not Analyze: {occurrence.DiscoveredPath}";
        SelectAllCommand = DisabledCommand();
        ReplaceSelectionCommand = DisabledCommand(parameterized: true);
    }

    public Guid GroupId { get; }
    public ReviewCategoryKind Category { get; }
    public int MemberCount { get; }
    public int PositionInSet { get; }
    public int SizeOfSet { get; }
    public string Summary { get; }
    public string AutomationName => Summary;
    public string ItemStatus => $"{Category}; {MemberCount.ToString(CultureInfo.InvariantCulture)} comparison paths";
    public LazyReviewCollection<ScanOccurrenceViewModel> Members { get; }
    public AsyncCommand SelectAllCommand { get; }
    public AsyncCommand ReplaceSelectionCommand { get; }

    internal ScanOccurrenceViewModel? FindMember(long occurrenceId) =>
        memberIndexes.TryGetValue(occurrenceId, out var index) ? Members[index] : null;

    private static bool IsAlias(ImmutableArray<ScanFileOccurrence> members, int index)
    {
        var key = members[index].PhysicalFileKey;
        return key is not null && members.Take(index).Any(member => member.PhysicalFileKey == key);
    }

    private static bool IsAlias(ImmutableArray<ReviewMemberSnapshot> members, int index)
    {
        var key = members[index].Occurrence.PhysicalFileKey;
        return key is not null && members.Take(index).Any(member => member.Occurrence.PhysicalFileKey == key);
    }

    private static string FormatSummary(ReviewCategoryKind category, int count) =>
        $"{category switch
        {
            ReviewCategoryKind.Exact => "Exact duplicate",
            ReviewCategoryKind.Near => "Near duplicate",
            ReviewCategoryKind.Uncertain => "Uncertain",
            _ => "Could Not Analyze",
        }} group: {count.ToString(CultureInfo.InvariantCulture)} paths";

    private static AsyncCommand DisabledCommand(bool parameterized = false) => parameterized
        ? new AsyncCommand(_ => Task.CompletedTask, canExecute: () => false)
        : new AsyncCommand(() => Task.CompletedTask, canExecute: () => false);

    private static ImmutableArray<long> RequireIds(object? parameter) => parameter switch
    {
        ImmutableArray<long> ids => ids,
        IEnumerable<long> ids => ids.Distinct().Order().ToImmutableArray(),
        _ => throw new ArgumentException("Review replacement requires occurrence identifiers.", nameof(parameter)),
    };
}
