using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;

namespace PhotoSift.App.Shell;

// Display values only. Historical evidence remains owned by the immutable
// ActionRecoverySnapshot and is never reused as cleanup authority.
public sealed class ActionRecordItemViewModel
{
    public ActionRecordItemViewModel(ActionRecordItemSnapshot snapshot, bool isVerifying, int position, int total)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ActionItemId = snapshot.Requested.ActionItemId;
        OriginalPath = snapshot.Requested.Safety.Item.DiscoveredPath;
        State = snapshot.State;
        ResultText = Result(snapshot.State);
        ReasonText = Reason(snapshot);
        IsVerifying = isVerifying;
        StatusText = isVerifying ? "Verifying current file state…" : $"{ResultText}; {ReasonText}";
        AutomationName = string.Create(CultureInfo.InvariantCulture,
            $"Action record item {position} of {total}; {OriginalPath}; {StatusText}");
    }

    public Guid ActionItemId { get; }
    public string OriginalPath { get; }
    public ActionRecordEvidenceState State { get; }
    public string ResultText { get; }
    public string ReasonText { get; }
    public bool IsVerifying { get; }
    public string StatusText { get; }
    public string AutomationName { get; }

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

    internal void Update(ActionRecordSnapshot next, Guid? activeActionItemId)
    {
        var previous = snapshot;
        snapshot = next;
        ProjectRows(next, activeActionItemId, previous);
        foreach (var property in new[] { nameof(RequestedUtc), nameof(CertaintyText), nameof(OutcomeMixText),
            nameof(RecoveredBytesText), nameof(HasUnknown) })
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
            if (previous.Items[index] != next.Items[index] || items[index].IsVerifying != verifying)
                items[index] = Row(next, activeActionItemId, index);
        }
    }

    private static ActionRecordItemViewModel Row(ActionRecordSnapshot record, Guid? activeActionItemId, int index) =>
        new(record.Items[index], activeActionItemId == record.Items[index].Requested.ActionItemId,
            index + 1, record.Items.Length);
}
