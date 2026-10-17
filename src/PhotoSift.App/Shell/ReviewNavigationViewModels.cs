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
    public string Description => Category switch
    {
        ReviewCategoryKind.Exact => "Identical copies: the same file saved more than once. Keeping one loses nothing.",
        ReviewCategoryKind.Near => "Look-alike: the same photo resized, recompressed or lightly edited.",
        ReviewCategoryKind.Uncertain => "Maybe similar: photos that look close. Check them yourself before removing any.",
        _ => "Files PhotoSift couldn't read or doesn't support. Nothing here can be removed.",
    };
    public string CountText => GroupCount == 1
        ? Category == ReviewCategoryKind.CouldNotAnalyze ? "1 file" : "1 set"
        : Category == ReviewCategoryKind.CouldNotAnalyze
            ? $"{GroupCount.ToString(CultureInfo.InvariantCulture)} files"
            : $"{GroupCount.ToString(CultureInfo.InvariantCulture)} sets";
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
            members[index], IsAlias(members, index), index + 1, members.Length));
        MemberCount = members.Length;
        Summary = FormatSummary(category, MemberCount);
        AutomationName = Summary;
        ItemStatus = $"{category}; {MemberCount.ToString(CultureInfo.InvariantCulture)} comparison paths";
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
                ImmutableArray.Create(members[index].Occurrence.Token.OccurrenceId)),
            index + 1, members.Length));
        MemberCount = members.Length;
        Summary = FormatSummary(category, MemberCount);
        SelectionStatus = FormatSelectionStatus(group.SelectedOccurrenceIds.Count);
        var physicalFileCount = members.Select(member => member.Occurrence.PhysicalFileKey)
            .Where(key => key is not null).Distinct().Count();
        AutomationName = category == ReviewCategoryKind.Exact
            ? $"Exact group {positionInSet.ToString(CultureInfo.InvariantCulture)} of {sizeOfSet.ToString(CultureInfo.InvariantCulture)}; {MemberCount.ToString(CultureInfo.InvariantCulture)} paths; {physicalFileCount.ToString(CultureInfo.InvariantCulture)} physical files"
            : Summary;
        ItemStatus = category == ReviewCategoryKind.Exact
            ? $"{group.SelectedOccurrenceIds.Count.ToString(CultureInfo.InvariantCulture)} selected; {ExactGroupViewModel.FormatSurvivor(group)}; {ExactGroupViewModel.FormatSelectionRecovery(group.Recovery)}"
            : $"{category}; {MemberCount.ToString(CultureInfo.InvariantCulture)} comparison paths";
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
        Members = new(1, _ => new ScanOccurrenceViewModel(occurrence,
            positionInSet: 1, sizeOfSet: 1));
        MemberCount = 1;
        Summary = $"Could Not Analyze: {occurrence.DiscoveredPath}";
        AutomationName = Summary;
        ItemStatus = "CouldNotAnalyze; 1 comparison path";
        SelectAllCommand = DisabledCommand();
        ReplaceSelectionCommand = DisabledCommand(parameterized: true);
    }

    public Guid GroupId { get; }
    public ReviewCategoryKind Category { get; }
    public int MemberCount { get; }
    public int PositionInSet { get; }
    public int SizeOfSet { get; }
    public string Summary { get; }
    public string SelectionStatus { get; } = string.Empty;
    public string AutomationName { get; private init; }
    public string ItemStatus { get; private init; }
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

    private static string FormatSelectionStatus(int selected) => selected switch
    {
        0 => "Nothing marked yet",
        1 => "1 photo marked for removal",
        _ => $"{selected.ToString(CultureInfo.InvariantCulture)} photos marked for removal",
    };

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
