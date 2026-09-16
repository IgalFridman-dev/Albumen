namespace PhotoSift.App.Shell;

public sealed record ScanOutcomeSummaryItemViewModel(
    string Label,
    long Count,
    string NextStep);
