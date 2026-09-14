using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;

namespace PhotoSift.App.Shell;

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

    internal void Update(ActionRecordItemSnapshot next, bool verifying)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Requested.ActionItemId != ActionItemId)
            throw new InvalidOperationException("A historical action row cannot change identity.");
        if (snapshot == next && isVerifying == verifying) return;
        snapshot = next;
        isVerifying = verifying;
        foreach (var property in new[] { nameof(State), nameof(ResultText), nameof(ReasonText),
            nameof(IsVerifying), nameof(StatusText), nameof(AutomationName) })
            PropertyChanged?.Invoke(this, new(property));
    }

    private static string Result(ActionRecordEvidenceState state) => state switch
    {
        ActionRecordEvidenceState.Recycled => "moved to Recycle Bin",
        ActionRecordEvidenceState.NotRemoved or ActionRecordEvidenceState.ConfirmedPresent => "not removed",
        _ => "outcome unknown pending revalidation",
    };

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
    public int UnknownCount => snapshot.Items.Count(item => item.State is ActionRecordEvidenceState.Requested
        or ActionRecordEvidenceState.Executing or ActionRecordEvidenceState.Unknown);

    internal void Update(ActionRecordSnapshot next, Guid? activeActionItemId)
    {
        var previous = snapshot;
        snapshot = next;
        ProjectRows(next, activeActionItemId, previous);
        foreach (var property in new[] { nameof(RequestedUtc), nameof(CertaintyText), nameof(OutcomeMixText),
            nameof(RecoveredBytesText), nameof(HasUnknown), nameof(UnknownCount) })
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

    private static ActionRecordItemViewModel Row(ActionRecordSnapshot record, Guid? activeActionItemId, int index) =>
        new(record.Items[index], activeActionItemId == record.Items[index].Requested.ActionItemId,
            index + 1, record.Items.Length);
}
