using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Albumen.Application.Action;
using Albumen.Domain.Action;

namespace Albumen.App.Shell;

// Display values only. Historical evidence remains owned by the immutable
// ActionRecoverySnapshot and is never reused as cleanup authority.
public sealed class ActionRecordItemViewModel : INotifyPropertyChanged
{
    private ActionRecordItemSnapshot snapshot;
    private bool isVerifying;
    private readonly int position;
    private readonly int total;

    public ActionRecordItemViewModel(ActionRecordItemSnapshot snapshot, bool isVerifying, int position, int total)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.snapshot = snapshot;
        this.isVerifying = isVerifying;
        this.position = position;
        this.total = total;
        ActionItemId = snapshot.Requested.ActionItemId;
        OriginalPath = snapshot.Requested.Safety.Item.DiscoveredPath;
        FileName = Path.GetFileName(OriginalPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : OriginalPath;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid ActionItemId { get; }
    public string OriginalPath { get; }
    public ActionRecordEvidenceState State => snapshot.State;
    public string ResultText => Result(snapshot.State);
    public string ReasonText => Reason(snapshot);
    public bool IsVerifying => isVerifying;
    public string StatusText => isVerifying ? "Verifying current file state…" : $"{ResultText}; {ReasonText}";
    public string AutomationName => string.Create(CultureInfo.InvariantCulture,
        $"Action record item {position} of {total}; {OriginalPath}; {StatusText}");

    // The History card shows the E1-S6 result words with the reason in plain words.
    public string FileName { get; }
    public ActionResultTone Tone => isVerifying ? ActionResultTone.Busy : snapshot.State switch
    {
        ActionRecordEvidenceState.Recycled => ActionResultTone.Done,
        ActionRecordEvidenceState.NotRemoved or ActionRecordEvidenceState.ConfirmedPresent => ActionResultTone.Skipped,
        _ => ActionResultTone.Warning,
    };
    public string DetailText => isVerifying ? "Verifying current file state…" : PlainReason(snapshot);
    public bool HasDetail => DetailText.Length != 0;

    internal void Update(ActionRecordItemSnapshot next, bool verifying)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Requested.ActionItemId != ActionItemId)
            throw new InvalidOperationException("A historical action row cannot change identity.");
        if (snapshot == next && isVerifying == verifying) return;
        snapshot = next;
        isVerifying = verifying;
        foreach (var property in new[] { nameof(State), nameof(ResultText), nameof(ReasonText),
            nameof(IsVerifying), nameof(StatusText), nameof(AutomationName),
            nameof(Tone), nameof(DetailText), nameof(HasDetail) })
            PropertyChanged?.Invoke(this, new(property));
    }

    private static string Result(ActionRecordEvidenceState state) => state switch
    {
        ActionRecordEvidenceState.Recycled => "moved to Recycle Bin",
        ActionRecordEvidenceState.NotRemoved or ActionRecordEvidenceState.ConfirmedPresent => "not removed",
        _ => "outcome unknown pending revalidation",
    };

    private static string PlainReason(ActionRecordItemSnapshot snapshot)
    {
        if (snapshot.RevalidationReason is { } revalidation)
            return revalidation switch
            {
                ActionRevalidationReason.OriginalFileConfirmed => "Checked again: the photo is still at its original path.",
                ActionRevalidationReason.OriginalPathMissing => "Checked again: nothing is at its original path any more.",
                ActionRevalidationReason.OriginalPathChanged => "Checked again: a different file is at its original path now.",
                ActionRevalidationReason.AccessUnavailable => "Checked again, but the file could not be read.",
                ActionRevalidationReason.LocationUnavailable => "Checked again, but its folder could not be reached.",
                ActionRevalidationReason.TimedOut => "Checking again took too long.",
                _ => "Checking again did not finish.",
            };
        var reason = snapshot.ExecutionReason == ActionSafetyReason.None
            ? string.Empty : ActionItemViewModel.DescribeReason(snapshot.ExecutionReason);
        return snapshot.State is ActionRecordEvidenceState.Requested or ActionRecordEvidenceState.Executing
            or ActionRecordEvidenceState.Unknown
            ? $"{reason} Albumen can't tell whether this photo moved. Revalidate it above.".TrimStart()
            : reason;
    }

    private static string Reason(ActionRecordItemSnapshot snapshot)
    {
        if (snapshot.RevalidationReason is { } revalidation)
            return revalidation switch
            {
                ActionRevalidationReason.OriginalFileConfirmed => "original-file-confirmed",
                ActionRevalidationReason.OriginalPathMissing => "original-path-missing",
                ActionRevalidationReason.OriginalPathChanged => "original-path-changed",
                ActionRevalidationReason.AccessUnavailable => "access-unavailable",
                ActionRevalidationReason.LocationUnavailable => "location-unavailable",
                ActionRevalidationReason.TimedOut => "revalidation-timed-out",
                _ => "revalidation-failed",
            };
        return snapshot.ExecutionReason == ActionSafetyReason.None
            ? "no additional reason"
            : snapshot.ExecutionReason.ToString();
    }
}

public sealed class ActionRecordViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<ActionRecordItemViewModel> items = [];
    private ActionRecordSnapshot snapshot;

    public ActionRecordViewModel(ActionRecordSnapshot snapshot, Guid? activeActionItemId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.snapshot = snapshot;
        RequestId = snapshot.RequestId;
        Items = new(items);
        ProjectRows(snapshot, activeActionItemId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid RequestId { get; }
    public DateTimeOffset RequestedUtc => snapshot.RequestedUtc;
    public ReadOnlyObservableCollection<ActionRecordItemViewModel> Items { get; }
    public string CertaintyText => snapshot.Summary.Certainty == ActionRecordCertainty.HasUnknown ? "Has unknown" : "Resolved";
    public string OutcomeMixText => snapshot.Summary.OutcomeMix switch
        {
            ActionRecordOutcomeMix.AllMoved => "All moved",
            ActionRecordOutcomeMix.NoneMoved => "None moved",
            _ => "Partial",
        };
    public string RecoveredBytesText => string.Create(CultureInfo.InvariantCulture,
        $"Recovered bytes: {snapshot.Summary.RecoveredBytes}");
    public bool HasUnknown => snapshot.Summary.Certainty == ActionRecordCertainty.HasUnknown;
    public string DateText => RequestedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    public string OutcomeLabel => HasUnknown ? "Needs a check" : snapshot.Summary.OutcomeMix switch
    {
        ActionRecordOutcomeMix.AllMoved => "All moved",
        ActionRecordOutcomeMix.NoneMoved => "Nothing moved",
        _ => "Some moved",
    };
    public ActionResultTone Tone => HasUnknown ? ActionResultTone.Warning : snapshot.Summary.OutcomeMix switch
    {
        ActionRecordOutcomeMix.AllMoved => ActionResultTone.Done,
        _ => ActionResultTone.Skipped,
    };
    public string SummaryText
    {
        get
        {
            var moved = snapshot.Items.Count(item => item.State == ActionRecordEvidenceState.Recycled);
            var skipped = snapshot.Items.Count(item => item.State is ActionRecordEvidenceState.NotRemoved
                or ActionRecordEvidenceState.ConfirmedPresent);
            var parts = new List<string>
            {
                Plural(snapshot.Items.Length, "photo", "photos"),
                string.Create(CultureInfo.InvariantCulture, $"{moved} moved"),
                string.Create(CultureInfo.InvariantCulture, $"{skipped} skipped"),
            };
            if (UnknownCount > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{UnknownCount} unknown"));
            if (snapshot.Summary.RecoveredBytes > 0)
                parts.Add($"about {ScanOccurrenceViewModel.FormatSize(snapshot.Summary.RecoveredBytes)} sent to the Recycle Bin");
            return string.Join(" · ", parts);
        }
    }
    public int UnknownCount => snapshot.Items.Count(item => item.State is ActionRecordEvidenceState.Requested
        or ActionRecordEvidenceState.Executing or ActionRecordEvidenceState.Unknown);

    internal void Update(ActionRecordSnapshot next, Guid? activeActionItemId)
    {
        var previous = snapshot;
        snapshot = next;
        ProjectRows(next, activeActionItemId, previous);
        foreach (var property in new[] { nameof(RequestedUtc), nameof(CertaintyText), nameof(OutcomeMixText),
            nameof(RecoveredBytesText), nameof(HasUnknown), nameof(UnknownCount), nameof(DateText),
            nameof(OutcomeLabel), nameof(Tone), nameof(SummaryText) })
            PropertyChanged?.Invoke(this, new(property));
    }

    private void ProjectRows(ActionRecordSnapshot next, Guid? activeActionItemId, ActionRecordSnapshot? previous = null)
    {
        if (previous is null || previous.Items.Length != next.Items.Length || items.Count != next.Items.Length)
        {
            items.Clear();
            for (var index = 0; index < next.Items.Length; index++)
                items.Add(Row(next, activeActionItemId, index));
            return;
        }
        for (var index = 0; index < next.Items.Length; index++)
        {
            var verifying = activeActionItemId == next.Items[index].Requested.ActionItemId;
            if (items[index].ActionItemId != next.Items[index].Requested.ActionItemId)
            {
                items.Clear();
                for (var rebuild = 0; rebuild < next.Items.Length; rebuild++)
                    items.Add(Row(next, activeActionItemId, rebuild));
                return;
            }
            items[index].Update(next.Items[index], verifying);
        }
    }

    private static string Plural(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

    private static ActionRecordItemViewModel Row(ActionRecordSnapshot record, Guid? activeActionItemId, int index) =>
        new(record.Items[index], activeActionItemId == record.Items[index].Requested.ActionItemId,
            index + 1, record.Items.Length);
}
