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
        var eligible = group.Members
            .Where(member => member.Eligibility == ReviewEligibilityState.Eligible)
            .Select(member => member.Occurrence.Token.OccurrenceId)
            .ToImmutableHashSet();
        var currentCandidate = group.Candidate with
        {
            Members = group.Members.Select(member => member.Occurrence).ToImmutableArray(),
        };
        var decision = SurvivorPolicy.Evaluate(currentCandidate, eligible, group.SelectedOccurrenceIds);
        var completeSelection = SurvivorPolicy.Evaluate(currentCandidate, eligible, eligible);
        if (decision.Kind == SurvivorDecisionKind.InconsistentEvidence
            || completeSelection.Kind == SurvivorDecisionKind.InconsistentEvidence)
            return "Survivor evidence is inconsistent; no survivor assurance is available.";
        if (decision.Kind == SurvivorDecisionKind.IneligibleSelection)
            return "Survivor status is unavailable because the Selection contains an ineligible occurrence.";
        if (!decision.ExternalSurvivors.IsEmpty)
            return $"A proven external hard-link survivor remains; its path is unknown to this Scan ({decision.ExternalSurvivors.Length.ToString(CultureInfo.InvariantCulture)} physical file).";
        if (decision.RemainingKnownPathOccurrences > 0)
            return $"{decision.RemainingKnownPathOccurrences.ToString(CultureInfo.InvariantCulture)} proven in-Scan survivor occurrence(s) remain.";
        return decision.Reason == SurvivorUnavailableReason.UnknownLinks
            ? "No proven survivor is available because hard-link evidence is unknown."
            : "No proven survivor remains; deliberate override is required.";
    }
}
