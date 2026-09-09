using System.Globalization;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.App.Shell;

public sealed class ScanOccurrenceViewModel
{
    public ScanOccurrenceViewModel(ScanFileOccurrence occurrence, bool isAlias = false)
        : this(occurrence, isAlias, null, false)
    {
    }

    internal ScanOccurrenceViewModel(
        ReviewMemberSnapshot member,
        bool isAlias,
        bool sealedSelection,
        Func<Task> toggle)
        : this(member.Occurrence, isAlias, new AsyncCommand(toggle), member.Selected)
    {
        Eligibility = member.Eligibility;
        UnavailableReason = member.Reason;
        CanSelect = member.Eligibility == ReviewEligibilityState.Eligible && !sealedSelection;
        EligibilityText = member.Eligibility switch
        {
            ReviewEligibilityState.PendingRevalidation => "Pending revalidation — not selectable",
            ReviewEligibilityState.Eligible => member.Selected ? "Eligible; selected" : "Eligible; not selected",
            ReviewEligibilityState.Unavailable => $"Unavailable ({member.Reason}) — not selectable",
            _ => throw new InvalidOperationException($"Unknown Review eligibility: {member.Eligibility}"),
        };
        AutomationName = $"{(member.Selected ? "Selected" : "Not selected")} occurrence: {member.Occurrence.DiscoveredPath}";
        ItemStatus = $"{EligibilityText}; {PathKind}; {Allocation}";
    }

    private ScanOccurrenceViewModel(
        ScanFileOccurrence occurrence,
        bool isAlias,
        AsyncCommand? toggle,
        bool selected)
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
        ToggleSelectionCommand = toggle;
        IsSelected = selected;
        EligibilityText = "Not part of the active Review";
        AutomationName = $"Occurrence: {occurrence.DiscoveredPath}";
        ItemStatus = $"{PathKind}; {Allocation}";
    }

    public long OccurrenceId { get; }
    public string DiscoveredPath { get; }
    public string ResolvedPath { get; }
    public string Outcome { get; }
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
}
