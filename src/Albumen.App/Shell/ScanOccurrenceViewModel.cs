using System.Globalization;
using System.IO;
using Albumen.Application.Review;
using Albumen.Domain.Scan;

namespace Albumen.App.Shell;

public sealed class ScanOccurrenceViewModel
{
    public ScanOccurrenceViewModel(
        ScanFileOccurrence occurrence,
        bool isAlias = false,
        int positionInSet = 0,
        int sizeOfSet = 0)
        : this(occurrence, isAlias, null, false, positionInSet, sizeOfSet)
    {
    }

    // markedElsewhereIn names the set that holds this photo's mark when it is marked in
    // another set ("Look-alike set 2"); the mark is shown here too and can't be changed here.
    // kindLine and uncertaintyLine are the set's evidence for this photo, shown on hover.
    internal ScanOccurrenceViewModel(
        ReviewMemberSnapshot member,
        bool isAlias,
        bool selectionEnabled,
        Func<Task> toggle,
        int positionInSet = 0,
        int sizeOfSet = 0,
        string? markedElsewhereIn = null,
        bool looksBlurrier = false,
        string kindLine = "",
        string uncertaintyLine = "")
        : this(member.Occurrence, isAlias, new AsyncCommand(toggle,
            canExecute: () => selectionEnabled && member.Eligibility == ReviewEligibilityState.Eligible
                && member.SelectedInGroup is null),
            member.Selected || member.SelectedInGroup is not null, positionInSet, sizeOfSet)
    {
        Eligibility = member.Eligibility;
        UnavailableReason = member.Reason;
        LooksBlurrier = looksBlurrier;
        KindLine = kindLine;
        UncertaintyLine = uncertaintyLine;
        IsMarkedElsewhere = member.SelectedInGroup is not null;
        MarkedElsewhereText = IsMarkedElsewhere ? $"Marked in {markedElsewhereIn ?? "another set"}" : string.Empty;
        CanSelect = member.Eligibility == ReviewEligibilityState.Eligible && selectionEnabled && !IsMarkedElsewhere;
        EligibilityText = IsMarkedElsewhere
            ? $"Marked for removal in {markedElsewhereIn ?? "another set"}"
            : member.Eligibility switch
            {
                ReviewEligibilityState.PendingRevalidation => "Being checked again, so it can't be marked yet",
                ReviewEligibilityState.Eligible => member.Selected ? "Marked for removal" : "Kept",
                ReviewEligibilityState.Unavailable => $"Can't be marked: {DescribeUnavailable(member.Reason)}",
                _ => throw new InvalidOperationException($"Unknown Review eligibility: {member.Eligibility}"),
            };
        if (member.Eligibility == ReviewEligibilityState.Eligible && !CanSelect && !IsMarkedElsewhere)
            EligibilityText += "; can't be changed right now";
        AutomationName = $"{(IsSelected ? "Marked for removal" : "Kept")}: {member.Occurrence.DiscoveredPath}";
        ItemStatus = $"{EligibilityText}; {PathKind}; {Allocation}"
            + (looksBlurrier ? $"; {BlurrierText}" : string.Empty);
    }

    // A search's match that is in no open set: it opens read-only, with nothing to decide here.
    internal static ScanOccurrenceViewModel InNoSet(ScanFileOccurrence occurrence) => new(occurrence, NotInAnySetText);

    private ScanOccurrenceViewModel(ScanFileOccurrence occurrence, string eligibilityText)
        : this(occurrence, false, null, false, 1, 1)
    {
        MatchesSearch = true;
        EligibilityText = eligibilityText;
        ItemStatus = $"{eligibilityText}; {ItemStatus}";
    }

    private ScanOccurrenceViewModel(
        ScanFileOccurrence occurrence,
        bool isAlias,
        AsyncCommand? toggle,
        bool selected,
        int positionInSet,
        int sizeOfSet)
    {
        OccurrenceId = occurrence.Token.OccurrenceId;
        DiscoveredPath = occurrence.DiscoveredPath;
        ResolvedPath = occurrence.ResolvedPath;
        Outcome = $"Enumerate: {occurrence.Enumerate.State} ({occurrence.Enumerate.Reason}); Exact: {occurrence.Exact.State} ({occurrence.Exact.Reason})";
        ByteLength = occurrence.Content?.ByteLength.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
        ByteCount = occurrence.Content?.ByteLength;
        Sharpness = occurrence.Sharpness;
        SizeText = ByteCount is { } bytes ? FormatSize(bytes) : "Size unknown";
        FileName = Path.GetFileName(occurrence.DiscoveredPath.TrimEnd('\\', '/')) is { Length: > 0 } name
            ? name : occurrence.DiscoveredPath;
        PhysicalIdentity = occurrence.PhysicalFileKey is { } key
            ? $"Volume {key.VolumeSerial:X}; file {key.FileId.High:X16}{key.FileId.Low:X16}"
            : "Unknown physical identity";
        PathKind = isAlias ? "Alias path to the same physical file" : "Physical file";
        IsAliasPath = isAlias;
        Allocation = $"Allocation: {occurrence.Allocation.AllocatedBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} bytes; links: {occurrence.Allocation.LinkCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; metadata {(occurrence.Allocation.Stable ? "stable" : "unavailable or unstable")}";
        (OutcomeLabel, NextStep) = DescribeOutcome(occurrence);
        ToggleSelectionCommand = toggle;
        IsSelected = selected;
        EligibilityText = "Not part of this review";
        AutomationName = $"Photo: {occurrence.DiscoveredPath}";
        ItemStatus = $"{PathKind}; {Allocation}";
        PositionInSet = positionInSet;
        SizeOfSet = sizeOfSet;
    }

    public long OccurrenceId { get; }
    public string DiscoveredPath { get; }
    public string ResolvedPath { get; }
    public string Outcome { get; }
    public string OutcomeLabel { get; }
    public string NextStep { get; }
    public string ByteLength { get; }
    public long? ByteCount { get; }
    public double? Sharpness { get; }
    // Blur evidence, shown as a badge: never a mark or a choice.
    public bool LooksBlurrier { get; }
    public const string BlurrierText = "Blurrier than the sharpest photo in this set";
    // No Keeper advice (E3-S5): the "Suggested best" badge is a hint, never a choice.
    public const string RecommendationText = "Albumen doesn't mark photos for you. You choose what to remove.";
    public const string NotInAnySetText = "Not in any set: nothing to decide";
    // Why the photo is in its set, for the facts shown on hover: "The files are identical, byte
    // for byte." or the picture and color difference; empty when the set holds no evidence for it.
    public string KindLine { get; } = string.Empty;
    // Said only of Maybe-similar sets.
    public string UncertaintyLine { get; } = string.Empty;
    // "Marked in Look-alike set 2" on the tile of a photo whose mark another set holds.
    public string MarkedElsewhereText { get; } = string.Empty;
    // True while a search filters Review and this photo is one of its matches ("Matches" chip).
    public bool MatchesSearch { get; init; }
    public string SizeText { get; }
    public string FileName { get; }
    public string PhysicalIdentity { get; }
    public string PathKind { get; }
    public bool IsAliasPath { get; }
    public string Allocation { get; }
    public ReviewEligibilityState Eligibility { get; }
    public ReviewUnavailableReason UnavailableReason { get; }
    // True when the photo is marked for removal, in this set or in another one.
    public bool IsSelected { get; }
    public bool IsMarkedElsewhere { get; }
    public bool CanSelect { get; }
    public string EligibilityText { get; }
    public string AutomationName { get; }
    public string ItemStatus { get; }
    public AsyncCommand? ToggleSelectionCommand { get; }
    public int PositionInSet { get; }
    public int SizeOfSet { get; }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("0", CultureInfo.InvariantCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
        _ => $"{(bytes / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture)} GB",
    };

    private static string DescribeUnavailable(ReviewUnavailableReason reason) => reason switch
    {
        ReviewUnavailableReason.Missing => "the file is missing",
        ReviewUnavailableReason.Changed => "the file changed since the scan",
        ReviewUnavailableReason.AccessDenied => "Windows denied access to the file",
        ReviewUnavailableReason.SharingViolation => "another app has the file open",
        ReviewUnavailableReason.UnsafeLocation => "the file is no longer in a safe location",
        ReviewUnavailableReason.UnknownSafety => "Albumen can't confirm it is safe to remove",
        ReviewUnavailableReason.ReadFailure => "the file couldn't be read",
        ReviewUnavailableReason.Canceled => "the check was canceled",
        ReviewUnavailableReason.Stale => "the scan is out of date",
        ReviewUnavailableReason.StoreUnavailable => "local data is unavailable",
        _ => $"unavailable ({reason})",
    };

    private static (string Label, string NextStep) DescribeOutcome(ScanFileOccurrence occurrence)
    {
        var state = FirstFailure(occurrence.VisualFeatures, occurrence.NearDuplicate)?.State
            ?? (occurrence.Exact.State == ScanStageState.NotApplicable
                ? occurrence.Enumerate.State
                : occurrence.Exact.State);
        return state switch
        {
            ScanStageState.Unsupported => (
                "Unsupported",
                "Use a supported JPEG or PNG file, then start a fresh Scan."),
            ScanStageState.Canceled => (
                "Canceled/unprocessed",
                "Start a fresh Scan to analyze this file."),
            ScanStageState.FailedRetryable or ScanStageState.FailedTerminal => (
                "Failed",
                "Check file access and health, then start a fresh Scan."),
            ScanStageState.Pending or ScanStageState.Running => (
                "Not finished",
                "Wait for this Scan to finish, or cancel it and start a fresh Scan."),
            _ => (
                "Unique photo",
                "No duplicate candidate was found. Keep it, or scan again after the file changes."),
        };
    }

    private static StageOutcome? FirstFailure(params StageOutcome[] outcomes) =>
        outcomes.FirstOrDefault(outcome => outcome.State is ScanStageState.Unsupported
            or ScanStageState.FailedRetryable or ScanStageState.FailedTerminal
            or ScanStageState.Canceled);
}
