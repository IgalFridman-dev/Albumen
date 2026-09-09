using System.Collections.Immutable;
using System.Globalization;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.App.Shell;

public sealed class ExactGroupViewModel
{
    public ExactGroupViewModel(CandidateGroup group)
    {
        var seen = new HashSet<PhysicalFileKey>();
        Members = group.Members.OrderBy(member => member.Token.OccurrenceId)
            .Select(member => new ScanOccurrenceViewModel(member,
                member.PhysicalFileKey is { } key && !seen.Add(key))).ToImmutableArray();
        PhysicalFileCount = seen.Count;
        AliasCount = Members.Length - PhysicalFileCount;
        Summary = $"Exact duplicate group: {PhysicalFileCount} physical files, {Members.Length} paths, {AliasCount} aliases";
        PotentialRecovery = FormatPotentialRecovery(group.PotentialRecovery);
        SelectionRecovery = "Estimated recovery for this Selection: no active Review.";
        SurvivorText = "No active survivor decision.";
        AutomationName = Summary;
        ItemStatus = $"0 selected; {SelectionRecovery}";
        SelectedOccurrenceIds = [];
        SelectAllCommand = new AsyncCommand(() => Task.CompletedTask);
        ReplaceSelectionCommand = new AsyncCommand(_ => Task.CompletedTask);
    }

    internal ExactGroupViewModel(
        ReviewGroupSnapshot group,
        long selectionRevision,
        int position,
        int count,
        Func<SelectionCommandKind, ImmutableArray<long>, Task> changeSelection)
    {
        GroupId = group.Candidate.GroupId;
        GroupRevision = group.Candidate.GroupRevision;
        SelectionRevision = selectionRevision;
        SelectedOccurrenceIds = group.SelectedOccurrenceIds;
        var seen = new HashSet<PhysicalFileKey>();
        Members = group.Members.OrderBy(member => member.Occurrence.Token.OccurrenceId)
            .Select(member => new ScanOccurrenceViewModel(member,
                member.Occurrence.PhysicalFileKey is { } key && !seen.Add(key),
                group.Sealed,
                () => changeSelection(SelectionCommandKind.Toggle,
                    ImmutableArray.Create(member.Occurrence.Token.OccurrenceId))))
            .ToImmutableArray();
        PhysicalFileCount = seen.Count;
        AliasCount = Members.Length - PhysicalFileCount;
        Summary = $"Exact duplicate group: {PhysicalFileCount} physical files, {Members.Length} paths, {AliasCount} aliases";
        PotentialRecovery = FormatPotentialRecovery(group.Candidate.PotentialRecovery);
        SelectionRecovery = FormatSelectionRecovery(group.Recovery);
        SurvivorText = FormatSurvivor(group);
        AutomationName = $"Exact group {position.ToString(CultureInfo.InvariantCulture)} of {count.ToString(CultureInfo.InvariantCulture)}; {Members.Length.ToString(CultureInfo.InvariantCulture)} paths; {PhysicalFileCount.ToString(CultureInfo.InvariantCulture)} physical files";
        ItemStatus = $"{SelectedOccurrenceIds.Count.ToString(CultureInfo.InvariantCulture)} selected; {SurvivorText}; {SelectionRecovery}";
        var selectable = group.Members.Where(member => member.Eligibility == ReviewEligibilityState.Eligible)
            .Select(member => member.Occurrence.Token.OccurrenceId).Order().ToImmutableArray();
        SelectAllCommand = new AsyncCommand(() =>
            changeSelection(SelectionCommandKind.ReplaceFocusedGroup, selectable));
        ReplaceSelectionCommand = new AsyncCommand(parameter =>
            changeSelection(SelectionCommandKind.ReplaceFocusedGroup, RequireIds(parameter)));
    }

    public Guid GroupId { get; }
    public long GroupRevision { get; }
    public long SelectionRevision { get; }
    public int PhysicalFileCount { get; }
    public int AliasCount { get; }
    public string Summary { get; }
    public string PotentialRecovery { get; }
    public string SelectionRecovery { get; }
    public string SurvivorText { get; }
    public string AutomationName { get; }
    public string ItemStatus { get; }
    public ImmutableHashSet<long> SelectedOccurrenceIds { get; }
    public ImmutableArray<ScanOccurrenceViewModel> Members { get; }
    public AsyncCommand SelectAllCommand { get; }
    public AsyncCommand ReplaceSelectionCommand { get; }

    private static ImmutableArray<long> RequireIds(object? parameter) => parameter switch
    {
        ImmutableArray<long> ids => ids,
        IEnumerable<long> ids => ids.Distinct().Order().ToImmutableArray(),
        _ => throw new ArgumentException("Review replacement requires occurrence identifiers.", nameof(parameter)),
    };

    private static string FormatPotentialRecovery(PotentialRecovery recovery) => recovery.Bytes is { } bytes
        ? $"Potential recovery: up to {bytes.ToString(CultureInfo.InvariantCulture)} bytes; no removal plan selected."
        : $"Potential recovery: unavailable ({recovery.Reason}).";

    private static string FormatSelectionRecovery(SelectionRecovery recovery) => recovery.Bytes is { } bytes
        ? $"Estimated recovery for this Selection: {bytes.ToString(CultureInfo.InvariantCulture)} bytes."
        : $"Estimated recovery for this Selection: unavailable ({recovery.Reason}).";

    private static string FormatSurvivor(ReviewGroupSnapshot group)
    {
        var pathsByPhysical = group.Members.Where(member => member.Occurrence.PhysicalFileKey is not null)
            .GroupBy(member => member.Occurrence.PhysicalFileKey!.Value)
            .ToDictionary(values => values.Key,
                values => values.Select(value => value.Occurrence.ResolvedPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var external = group.Members.Select(member => member.Occurrence)
            .Where(occurrence => occurrence.PhysicalFileKey is { } key
                && occurrence.Allocation is { Stable: true, LinkCount: { } links }
                && pathsByPhysical.TryGetValue(key, out var known) && links > known)
            .Select(occurrence => occurrence.PhysicalFileKey!.Value).Distinct().Count();
        if (external > 0)
            return $"A proven external hard-link survivor remains; its path is unknown to this Scan ({external.ToString(CultureInfo.InvariantCulture)} physical file).";
        var remaining = group.Members.Count(member => member.Eligibility == ReviewEligibilityState.Eligible && !member.Selected);
        return remaining > 0
            ? $"{remaining.ToString(CultureInfo.InvariantCulture)} proven in-Scan survivor occurrence(s) remain."
            : "No proven survivor remains; deliberate override is required.";
    }
}
