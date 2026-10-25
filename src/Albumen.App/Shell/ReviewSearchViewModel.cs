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
/// Review's photo search: the search bar's prompt and the filter its result puts on Review.
/// Ephemeral and read-only: the ranking comes from <see cref="LikelyMatchesQuery"/>, and nothing
/// here changes a set, a mark or a cleanup.
/// </summary>
public sealed class ReviewSearchViewModel : INotifyPropertyChanged
{
    private readonly LikelyMatchesQuery? query;
    private AlbumenSnapshot? snapshot;
    private string prompt = string.Empty;
    private LikelyMatchesResult? result;
    private bool searching;
    private long request;
    private int shownCount;

    public ReviewSearchViewModel(LikelyMatchesQuery? query)
    {
        this.query = query;
        SearchCommand = new AsyncCommand(SearchAsync, allowConcurrentExecutions: true, canExecute: () => CanSearch);
        // The clear button and Esc: Esc also clears a prompt that was typed but not searched.
        ShowAllCommand = new AsyncCommand(() =>
        {
            Clear();
            return Task.CompletedTask;
        }, canExecute: () => IsActive || prompt.Length != 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a search or Show all changes <see cref="Filter"/>, so Review is projected again.</summary>
    internal event Action? FilterChanged;

    /// <summary>The search Review is filtered by: set by a search that ran, empty or not.</summary>
    public ReviewSearchFilter? Filter { get; private set; }

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
            ShowAllCommand.NotifyCanExecuteChanged();
        }
    }

    private SemanticSnapshot Index => snapshot?.Scan.Semantic ?? SemanticSnapshot.Idle;

    public bool CanSearch => query is not null && Index.State == SemanticState.Ready;

    /// <summary>True while a search filters Review, whether or not it matched anything.</summary>
    public bool IsActive => result?.Outcome is LikelyMatchesOutcome.Matches or LikelyMatchesOutcome.NoMatches;

    /// <summary>Inside the empty bar: the invitation to search, or why search can't run.</summary>
    public string PlaceholderText => CanSearch ? "Find photos by what they show" : StatusText;

    /// <summary>The line under the bar shows only when it has news: not while search is idle.</summary>
    public bool HasStatus => StatusText != IdleText;

    private const string IdleText = "Describe what's in a photo, for example “at sea”, and press Enter.";

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

    private string ReadyText
    {
        get
        {
            if (searching) return "Searching…";
            return result switch
            {
                { Outcome: LikelyMatchesOutcome.Matches } when shownCount == 1 =>
                    $"1 likely match in this Scan for “{result.Prompt}”",
                { Outcome: LikelyMatchesOutcome.Matches } when shownCount > 1 =>
                    $"{Count(shownCount)} likely matches in this Scan for “{result.Prompt}”, most likely first",
                { Outcome: LikelyMatchesOutcome.Matches or LikelyMatchesOutcome.NoMatches } =>
                    $"No likely matches in this Scan for “{result.Prompt}”. Try other words.",
                { Outcome: LikelyMatchesOutcome.TooLong } =>
                    $"Use at most {Count(LikelyMatchesQuery.MaximumPromptLength)} characters. This one has {Count(result.Prompt.Length)}.",
                { Outcome: LikelyMatchesOutcome.Unavailable or LikelyMatchesOutcome.Failed } =>
                    $"Search didn't work: {Reason(result.Reason)} Everything else in Review works.",
                _ => IdleText,
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
            SetResult(null);
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
        var before = Filter;
        SetResult(found);
        Project();
        if (!ReferenceEquals(before, Filter)) FilterChanged?.Invoke();
    }

    private void Clear()
    {
        var wasActive = IsActive;
        request++;
        searching = false;
        SetResult(null);
        Prompt = string.Empty;
        Project();
        if (wasActive) FilterChanged?.Invoke();
    }

    // Only a search that ran filters Review; one that could not run leaves it unfiltered.
    private void SetResult(LikelyMatchesResult? value)
    {
        result = value;
        Filter = value is { Outcome: LikelyMatchesOutcome.Matches or LikelyMatchesOutcome.NoMatches }
            ? new(value.Prompt, value.Matches)
            : null;
    }

    // The matches Review still shows somewhere, for the status line: a photo a cleanup moved to the
    // Recycle Bin has left every set and is not counted.
    private void Project()
    {
        shownCount = result is { Outcome: LikelyMatchesOutcome.Matches } found && snapshot is { } current
            ? CountInReview(found, current)
            : 0;
        Changed();
    }

    private static int CountInReview(LikelyMatchesResult found, AlbumenSnapshot current)
    {
        if (current.Review.Pin is null) return found.Matches.Length;
        var reviewed = current.Review.Groups.SelectMany(group => group.Members)
            .Select(member => member.Occurrence.Token.OccurrenceId).ToHashSet();
        var grouped = current.Scan.Groups.SelectMany(group => group.Members)
            .Select(member => member.Token.OccurrenceId).ToHashSet();
        return found.Matches.Count(match => reviewed.Contains(match.OccurrenceId) || !grouped.Contains(match.OccurrenceId));
    }

    private void Changed()
    {
        foreach (var name in new[] { nameof(CanSearch), nameof(IsActive), nameof(StatusText),
            nameof(PlaceholderText), nameof(HasStatus), nameof(ScopeText) })
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

/// <summary>
/// A search that filters Review: its prompt and the photos <see cref="LikelyMatchesQuery"/> ranked
/// for it, best first, with each photo's score.
/// </summary>
public sealed record ReviewSearchFilter(string Prompt, ImmutableArray<LikelyMatch> Ranked)
{
    public ImmutableDictionary<long, float> Matches { get; } = ScoresOf(Ranked);

    private static ImmutableDictionary<long, float> ScoresOf(ImmutableArray<LikelyMatch> ranked)
    {
        var scores = ImmutableDictionary.CreateBuilder<long, float>();
        foreach (var match in ranked)
        {
            if (!scores.ContainsKey(match.OccurrenceId)) scores.Add(match.OccurrenceId, match.Score);
        }
        return scores.ToImmutable();
    }
}
