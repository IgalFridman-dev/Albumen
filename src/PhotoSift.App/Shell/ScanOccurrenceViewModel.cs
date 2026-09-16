using System.Globalization;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.App.Shell;

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

    internal ScanOccurrenceViewModel(
        ReviewMemberSnapshot member,
        bool isAlias,
        bool selectionEnabled,
        Func<Task> toggle,
        int positionInSet = 0,
        int sizeOfSet = 0)
        : this(member.Occurrence, isAlias, new AsyncCommand(toggle,
            canExecute: () => selectionEnabled && member.Eligibility == ReviewEligibilityState.Eligible),
            member.Selected, positionInSet, sizeOfSet)
    {
        Eligibility = member.Eligibility;
        UnavailableReason = member.Reason;
        CanSelect = member.Eligibility == ReviewEligibilityState.Eligible && selectionEnabled;
        EligibilityText = member.Eligibility switch
        {
            ReviewEligibilityState.PendingRevalidation => "Pending revalidation — not selectable",
            ReviewEligibilityState.Eligible => member.Selected ? "Eligible; selected" : "Eligible; not selected",
            ReviewEligibilityState.Unavailable => $"Unavailable ({member.Reason}) — not selectable",
            _ => throw new InvalidOperationException($"Unknown Review eligibility: {member.Eligibility}"),
        };
        if (member.Eligibility == ReviewEligibilityState.Eligible && !CanSelect)
            EligibilityText += "; Review selection unavailable";
        AutomationName = $"{(member.Selected ? "Selected" : "Not selected")} occurrence: {member.Occurrence.DiscoveredPath}";
        ItemStatus = $"{EligibilityText}; {PathKind}; {Allocation}";
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
        PhysicalIdentity = occurrence.PhysicalFileKey is { } key
            ? $"Volume {key.VolumeSerial:X}; file {key.FileId.High:X16}{key.FileId.Low:X16}"
            : "Unknown physical identity";
        PathKind = isAlias ? "Alias path to the same physical file" : "Physical file";
        Allocation = $"Allocation: {occurrence.Allocation.AllocatedBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} bytes; links: {occurrence.Allocation.LinkCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; metadata {(occurrence.Allocation.Stable ? "stable" : "unavailable or unstable")}";
        (OutcomeLabel, NextStep) = DescribeOutcome(occurrence);
        ToggleSelectionCommand = toggle;
        IsSelected = selected;
        EligibilityText = "Not part of the active Review";
        AutomationName = $"Occurrence: {occurrence.DiscoveredPath}";
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
    public string PhysicalIdentity { get; }
    public string PathKind { get; }
    public string Allocation { get; }
    public ReviewEligibilityState Eligibility { get; }
    public ReviewUnavailableReason UnavailableReason { get; }
    public bool IsSelected { get; }
    public bool CanSelect { get; }
    public string EligibilityText { get; }
    public string AutomationName { get; }
    public string ItemStatus { get; }
    public AsyncCommand? ToggleSelectionCommand { get; }
    public int PositionInSet { get; }
    public int SizeOfSet { get; }

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
                "Outstanding",
                "Wait for this Scan to finish, or cancel it and start a fresh Scan."),
            _ => (
                "Analyzed acceptable",
                "No duplicate candidate was found. Keep it, or scan again after the file changes."),
        };
    }

    private static StageOutcome? FirstFailure(params StageOutcome[] outcomes) =>
        outcomes.FirstOrDefault(outcome => outcome.State is ScanStageState.Unsupported
            or ScanStageState.FailedRetryable or ScanStageState.FailedTerminal
            or ScanStageState.Canceled);
}
