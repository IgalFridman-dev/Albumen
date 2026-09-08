using System.Globalization;
using PhotoSift.Domain.Scan;

namespace PhotoSift.App.Shell;

public sealed class ScanOccurrenceViewModel(ScanFileOccurrence occurrence, bool isAlias = false)
{
    public string DiscoveredPath { get; } = occurrence.DiscoveredPath;
    public string ResolvedPath { get; } = occurrence.ResolvedPath;
    public string Outcome { get; } = $"Enumerate: {occurrence.Enumerate.State} ({occurrence.Enumerate.Reason}); Exact: {occurrence.Exact.State} ({occurrence.Exact.Reason})";
    public string ByteLength { get; } = occurrence.Content?.ByteLength.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
    public string PhysicalIdentity { get; } = occurrence.PhysicalFileKey is { } key
        ? $"Volume {key.VolumeSerial:X}; file {key.FileId.High:X16}{key.FileId.Low:X16}" : "Unknown physical identity";
    public string PathKind { get; } = isAlias ? "Alias path to the same physical file" : "Physical file";
    public string Allocation { get; } = $"Allocation: {occurrence.Allocation.AllocatedBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} bytes; links: {occurrence.Allocation.LinkCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; metadata {(occurrence.Allocation.Stable ? "stable" : "unavailable or unstable")}";
}
