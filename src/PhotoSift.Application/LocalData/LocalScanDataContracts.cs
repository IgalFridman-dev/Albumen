namespace PhotoSift.Application.LocalData;

public enum ScanDataClearOutcome
{
    Cleared,
    BlockedByActionReconciliation,
    RetainedDatabaseFailure,
}

public sealed record ScanDataClearResult(
    ScanDataClearOutcome Outcome,
    long ClearedScanCount,
    long? RetainedScanCount,
    string Message);

public interface ILocalScanDataControl
{
    ValueTask<ScanDataClearResult> ClearAsync(CancellationToken cancellationToken);
}
