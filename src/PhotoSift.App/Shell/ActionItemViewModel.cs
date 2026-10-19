using System.Globalization;
using System.IO;
using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;

namespace PhotoSift.App.Shell;

// Display values only. Every row is copied from one immutable owner snapshot.
public sealed class ActionItemViewModel
{
    public ActionItemViewModel(ActionItemSnapshot snapshot, int position, int total)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        OccurrenceId = snapshot.Item.Occurrence.OccurrenceId;
        OriginalPath = snapshot.Item.DiscoveredPath;
        State = snapshot.State;
        Reason = snapshot.Reason;
        FileName = Path.GetFileName(OriginalPath.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : OriginalPath;
        AutomationName = $"Item {position.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)}; Windows Recycle Bin; {OriginalPath}";
    }

    public long OccurrenceId { get; }
    public string OriginalPath { get; }
    public ActionItemState State { get; }
    public ActionSafetyReason Reason { get; }
    public string AutomationName { get; }
    public string StateText => State switch
    {
        ActionItemState.Waiting => "Waiting",
        ActionItemState.NotRequested => "Not requested",
        ActionItemState.Requested => "Requested",
        ActionItemState.InterruptedBeforeExecution => "Interrupted before execution",
        ActionItemState.Executing => "Executing",
        ActionItemState.Recycled => "Recycled",
        ActionItemState.NotRemoved => "Not removed",
        ActionItemState.UnknownPendingRevalidation => "Unknown pending revalidation",
        _ => "Unknown state",
    };
    public string FileName { get; }
    public string ReasonText => DescribeReason(Reason);

    // Plain words for the card; StateText and CertaintyText stay the recorded detail.
    public string ResultLabel => State switch
    {
        ActionItemState.Waiting => "Waiting",
        ActionItemState.Requested => "Getting ready",
        ActionItemState.Executing => "Moving…",
        ActionItemState.Recycled => "In the Recycle Bin",
        ActionItemState.NotRemoved => "Skipped",
        ActionItemState.NotRequested => "Not moved",
        ActionItemState.InterruptedBeforeExecution => "Stopped before moving",
        _ => "Outcome unknown",
    };
    public ActionResultTone Tone => State switch
    {
        ActionItemState.Recycled => ActionResultTone.Done,
        ActionItemState.NotRemoved or ActionItemState.NotRequested => ActionResultTone.Skipped,
        ActionItemState.Waiting => ActionResultTone.Waiting,
        ActionItemState.Requested or ActionItemState.Executing => ActionResultTone.Busy,
        _ => ActionResultTone.Warning,
    };
    public string DetailText
    {
        get
        {
            var reason = Reason == ActionSafetyReason.None ? string.Empty : ReasonText + " ";
            return State switch
            {
                ActionItemState.NotRemoved or ActionItemState.NotRequested => $"{reason}The photo was left where it is.",
                ActionItemState.InterruptedBeforeExecution =>
                    $"{reason}The cleanup stopped before this photo was sent to the Recycle Bin.",
                ActionItemState.UnknownPendingRevalidation =>
                    $"{reason}PhotoSift can't tell yet whether this photo moved. It needs a later check.",
                _ => reason.TrimEnd(),
            };
        }
    }
    public bool HasDetail => DetailText.Length != 0;
    public string CertaintyText => State switch
    {
        ActionItemState.Waiting => "Not requested; no Recycle Bin operation has been started for this item.",
        ActionItemState.NotRequested => "Not requested; this Action did not start a Recycle Bin operation for this item.",
        ActionItemState.Requested => "Durable Requested intent is recorded; execution has not been confirmed.",
        ActionItemState.InterruptedBeforeExecution => "Durable Requested intent remains without an Executing record; this item was interrupted before execution.",
        ActionItemState.Executing => "The outcome is uncertain until the Windows operation settles and its result is durably recorded.",
        ActionItemState.Recycled => "Windows confirmed sending this original path to Recycle Bin. This does not measure freed disk space or physical-file deletion.",
        ActionItemState.NotRemoved => "The original was affirmatively confirmed unchanged by this operation.",
        _ => "The outcome is uncertain: the original may have moved. A later safe review is required.",
    };
    public string ItemStatus => $"{StateText}; {ReasonText} {CertaintyText}";

    internal static string DescribeReason(ActionSafetyReason reason) => reason switch
    {
        ActionSafetyReason.None => "No additional reason.",
        ActionSafetyReason.StalePin => "The approved file or review revision no longer matches.",
        ActionSafetyReason.Missing => "The original path is missing.",
        ActionSafetyReason.Changed => "The file changed since approval.",
        ActionSafetyReason.AccessDenied => "Access to the file was denied.",
        ActionSafetyReason.SharingViolation => "Another process prevents the required protected access.",
        ActionSafetyReason.UnsafeLocation => "The current location cannot be used safely.",
        ActionSafetyReason.UnknownSafety => "Current safety checks could not establish a safe operation.",
        ActionSafetyReason.Synchronized => "The current location is synchronized.",
        ActionSafetyReason.Placeholder => "The file is a cloud placeholder.",
        ActionSafetyReason.ReadOnly => "The file is read-only.",
        ActionSafetyReason.PermissionDenied => "Windows denied permission for the operation.",
        ActionSafetyReason.Unavailable => "The file or Windows operation is unavailable.",
        ActionSafetyReason.CannotRecycle => "Windows cannot send this item to Recycle Bin.",
        ActionSafetyReason.CallbackContradiction => "Windows reported conflicting operation evidence.",
        ActionSafetyReason.InconsistentLinks => "Current hard-link evidence is inconsistent.",
        ActionSafetyReason.NoCurrentSurvivor => "Current checks cannot confirm a remaining copy.",
        ActionSafetyReason.OverrideMissing => "The required deliberate override evidence is missing.",
        ActionSafetyReason.OverrideMismatch => "The deliberate override evidence does not exactly match this approval.",
        ActionSafetyReason.DependentOutcomeUnknown => "A dependent item has an uncertain outcome; this item was stopped.",
        ActionSafetyReason.StoreUnavailable => "The required saved evidence is unavailable.",
        ActionSafetyReason.JournalConflict => "The Action journal contains conflicting durable records.",
        ActionSafetyReason.JournalUnavailable => "The Action journal could not durably record the operation.",
        ActionSafetyReason.Interrupted => "The operation was interrupted.",
        ActionSafetyReason.ProgrammingFault => "An internal fault stopped this Action.",
        _ => "An unrecognized reason requires a later safe review.",
    };
}

public enum ActionResultTone { Waiting, Busy, Done, Skipped, Warning }
