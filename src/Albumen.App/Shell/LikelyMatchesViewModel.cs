using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Application.Shell;
using Albumen.Domain.Scan;

namespace Albumen.App.Shell;

/// <summary>
/// Review's prompt box and its ranked list. Ephemeral and read-only: the ranking comes from
/// <see cref="LikelyMatchesQuery"/>, and nothing here changes a set, a mark or a cleanup.
/// </summary>
public sealed class LikelyMatchesViewModel : INotifyPropertyChanged
{
    private readonly LikelyMatchesQuery? query;
    private AlbumenSnapshot? snapshot;
    private string prompt = string.Empty;
    private LikelyMatchesResult? result;
    private bool searching;
    private long request;
    private ImmutableDictionary<long, PhotoSet> sets = ImmutableDictionary<long, PhotoSet>.Empty;
    private ImmutableHashSet<long> selected = [];
    private ImmutableHashSet<long> grouped = [];
    private ImmutableArray<LikelyMatch> shown = [];

    public LikelyMatchesViewModel(LikelyMatchesQuery? query)
    {
        this.query = query;
        SearchCommand = new AsyncCommand(SearchAsync, allowConcurrentExecutions: true, canExecute: () => CanSearch);
        ShowAllCommand = new AsyncCommand(() =>
        {
            Clear();
            return Task.CompletedTask;
        }, canExecute: () => IsActive);
        Results = new(0, _ => throw new InvalidOperationException());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised by Show all, so Review can restore the kind and set open before the search.</summary>
    internal event Action? Cleared;

    public AsyncCommand SearchCommand { get; }
    public AsyncCommand ShowAllCommand { get; }
    public static string PromptHelpText { get; } = string.Create(CultureInfo.InvariantCulture,
        $"Up to {LikelyMatchesQuery.MaximumPromptLength} characters. Searches only this Scan.");

    public string Prompt
    {
        get => prompt;
        set
        {
            if (prompt == (value ??= string.Empty)) return;
            prompt = value;
            OnPropertyChanged();
        }
    }

    private SemanticSnapshot Index => snapshot?.Scan.Semantic ?? SemanticSnapshot.Idle;

    public bool CanSearch => query is not null && Index.State == SemanticState.Ready;

    /// <summary>True while a ranked list replaces the hint: results or no results.</summary>
    public bool IsActive => result?.Outcome is LikelyMatchesOutcome.Matches or LikelyMatchesOutcome.NoMatches;

    public bool HasResults => !shown.IsEmpty;

    public LazyReviewCollection<LikelyMatchItemViewModel> Results { get; private set; }

    public string StatusText
    {
        get
        {
            if (query is null) return "Search isn't installed in this build. Everything else in Review works.";
            var index = Index;
            return index.State switch
            {
                SemanticState.Idle =>
                    "Search isn't ready for this Scan. Scan again to search photos by what they show.",
                SemanticState.Indexing =>
                    $"Getting photos ready for search: {Count(index.Indexed)} of {Count(index.Total)} ready.",
                SemanticState.Empty => "No photo in this Scan could be prepared for search.",
                SemanticState.Unavailable =>
                    $"Search isn't installed: {Reason(index.Reason)} Everything else in Review works.",
                SemanticState.Failed =>
                    $"Search isn't available for this Scan: {Reason(index.Reason)} Everything else in Review works.",
                _ => ReadyText,
            };
        }
    }

    /// <summary>The Scan's coverage, always shown while search is ready.</summary>
    public string ScopeText => Index is { State: SemanticState.Ready } index
        ? index.IsPartial
            ? $"Searches only this Scan: {Count(index.Indexed)} of {Count(index.Total)} photos could be prepared, so some photos can't be found."
            : $"Searches only the {Count(index.Indexed)} photos in this Scan."
        : string.Empty;

    /// <summary>Marks for removal that the list does not show; the cleanup still includes them.</summary>
    public string SelectionOutsideText
    {
        get
        {
            if (!IsActive || selected.IsEmpty) return string.Empty;
            var visible = shown.Select(match => match.OccurrenceId).ToHashSet();
            var outside = selected.Count(id => !visible.Contains(id));
            return outside switch
            {
                0 => string.Empty,
                1 => "1 photo marked for removal isn't in this list. Cleanup still includes it.",
                _ => $"{Count(outside)} photos marked for removal aren't in this list. Cleanup still includes them.",
            };
        }
    }

    public bool HasSelectionOutside => SelectionOutsideText.Length != 0;

    private string ReadyText
    {
        get
        {
            if (searching) return "Searching…";
            return result switch
            {
                { Outcome: LikelyMatchesOutcome.Matches } when shown.Length == 1 =>
                    $"1 likely match in this Scan for “{result.Prompt}”",
                { Outcome: LikelyMatchesOutcome.Matches } when shown.Length > 1 =>
                    $"{Count(shown.Length)} likely matches in this Scan for “{result.Prompt}”, most likely first",
                { Outcome: LikelyMatchesOutcome.Matches or LikelyMatchesOutcome.NoMatches } =>
                    $"No likely matches in this Scan for “{result.Prompt}”. Try other words.",
                { Outcome: LikelyMatchesOutcome.TooLong } =>
                    $"Use at most {Count(LikelyMatchesQuery.MaximumPromptLength)} characters. This one has {Count(result.Prompt.Length)}.",
                { Outcome: LikelyMatchesOutcome.Unavailable or LikelyMatchesOutcome.Failed } =>
                    $"Search didn't work: {Reason(result.Reason)} Everything else in Review works.",
                _ => "Describe what's in a photo, for example “at sea”, and press Enter.",
            };
        }
    }

    internal void Update(AlbumenSnapshot current)
    {
        var newScan = snapshot?.Scan.Pin != current.Scan.Pin;
        snapshot = current;
        if (newScan)
        {
            request++;
            searching = false;
            result = null;
            prompt = string.Empty;
            OnPropertyChanged(nameof(Prompt));
        }
        Project();
    }

    private async Task SearchAsync()
    {
        if (query is null || snapshot is not { } current) return;
        var id = ++request;
        searching = true;
        Changed();
        LikelyMatchesResult found;
        try
        {
            found = await query.RankAsync(current.Scan, Prompt, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            found = new(LikelyMatchesOutcome.Failed, LikelyMatchesQuery.Normalize(Prompt), [],
                "Searching failed unexpectedly.");
        }
        if (id != request) return;
        searching = false;
        if (found.Outcome == LikelyMatchesOutcome.Cleared)
        {
            Clear();
            return;
        }
        result = found;
        Project();
    }

    private void Clear()
    {
        var wasActive = IsActive;
        request++;
        searching = false;
        result = null;
        Prompt = string.Empty;
        Project();
        if (wasActive) Cleared?.Invoke();
    }

    // Photos that left Review (moved to the Recycle Bin, or in a set closed after a cleanup) are
    // not listed: only photos still in an open set, or never in any set, are.
    private void Project()
    {
        if (!IsActive || result is null || snapshot is not { } current)
        {
            shown = [];
            Results = new(0, _ => throw new InvalidOperationException());
            Changed();
            return;
        }
        var previousSets = sets;
        (sets, selected, grouped) = ProjectSets(current);
        var rows = current.Scan.Occurrences.ToDictionary(row => row.Token.OccurrenceId);
        var visible = result.Matches.Where(match => rows.ContainsKey(match.OccurrenceId)
                && (sets.ContainsKey(match.OccurrenceId) || !grouped.Contains(match.OccurrenceId)))
            .ToImmutableArray();
        // A mark changed elsewhere in Review keeps the list as it is, so its scroll position stays.
        var unchanged = Results.Count == visible.Length && visible.SequenceEqual(shown)
            && visible.All(match => Equals(previousSets.GetValueOrDefault(match.OccurrenceId),
                sets.GetValueOrDefault(match.OccurrenceId)));
        shown = visible;
        if (!unchanged)
        {
            var listed = sets;
            Results = new(visible.Length, index =>
            {
                var match = visible[index];
                return new(new ScanOccurrenceViewModel(rows[match.OccurrenceId], positionInSet: index + 1,
                    sizeOfSet: visible.Length), listed.GetValueOrDefault(match.OccurrenceId));
            });
        }
        Changed();
    }

    // Each photo's first open set, in the order Review lists them; the photos marked for removal;
    // and every photo the Scan put in a set.
    private static (ImmutableDictionary<long, PhotoSet>, ImmutableHashSet<long>, ImmutableHashSet<long>) ProjectSets(
        AlbumenSnapshot current)
    {
        var sets = ImmutableDictionary.CreateBuilder<long, PhotoSet>();
        var selected = ImmutableHashSet.CreateBuilder<long>();
        foreach (var category in new[] { ReviewCategoryKind.Exact, ReviewCategoryKind.Near, ReviewCategoryKind.Uncertain })
        {
            var position = 0;
            foreach (var group in current.Review.Groups.Where(group => !group.Closed && ShellViewModel.InCategory(group.Candidate, category)))
            {
                var set = new PhotoSet(category, group.Candidate.GroupId,
                    $"{ReviewCategoryNames.For(category)} set {(++position).ToString(CultureInfo.InvariantCulture)}");
                foreach (var member in group.Members)
                {
                    sets.TryAdd(member.Occurrence.Token.OccurrenceId, set);
                    if (member.Selected) selected.Add(member.Occurrence.Token.OccurrenceId);
                }
            }
        }
        var grouped = current.Scan.Groups.SelectMany(group => group.Members)
            .Select(member => member.Token.OccurrenceId).ToImmutableHashSet();
        return (sets.ToImmutable(), selected.ToImmutable(), grouped);
    }

    private void Changed()
    {
        foreach (var name in new[] { nameof(CanSearch), nameof(IsActive), nameof(HasResults), nameof(Results),
            nameof(StatusText), nameof(ScopeText), nameof(SelectionOutsideText), nameof(HasSelectionOutside) })
            OnPropertyChanged(name);
        SearchCommand.NotifyCanExecuteChanged();
        ShowAllCommand.NotifyCanExecuteChanged();
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Reason(string reason) =>
        reason.Length == 0 ? "something went wrong." : reason.EndsWith('.') ? reason : reason + ".";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>The Review set a likely match opens in.</summary>
public sealed record PhotoSet(ReviewCategoryKind Category, Guid GroupId, string Name);

public sealed class LikelyMatchItemViewModel(ScanOccurrenceViewModel photo, PhotoSet? set)
{
    public ScanOccurrenceViewModel Photo { get; } = photo;
    public PhotoSet? Set { get; } = set;
    public bool CanOpen => Set is not null;
    public string Context => Set?.Name ?? "Not in any set: nothing to decide";
    public string AutomationName => string.Create(CultureInfo.InvariantCulture,
        $"Likely match {Photo.PositionInSet} of {Photo.SizeOfSet}: {Photo.FileName}; {Context}");
}
