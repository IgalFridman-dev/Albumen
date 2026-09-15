using System.Collections.Immutable;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

internal static class ScanSnapshotProjector
{
    public static ScanSnapshot Create(ScanPin? pin, ScanStatus status, StageOutcome enumeration,
        ImmutableArray<ScanFileOccurrence> rows, ImmutableArray<CandidateGroup> groups)
    {
        long analyzed = 0, unsupported = 0, failed = 0, canceled = 0, outstanding = 0;
        foreach (var row in rows)
        {
            switch (row.Exact.State)
            {
                case ScanStageState.Succeeded: analyzed++; break;
                case ScanStageState.Unsupported:
                case ScanStageState.NotApplicable: unsupported++; break;
                case ScanStageState.FailedRetryable:
                case ScanStageState.FailedTerminal: failed++; break;
                case ScanStageState.Canceled: canceled++; break;
                case ScanStageState.Pending:
                case ScanStageState.Running: outstanding++; break;
            }
        }
        return new ScanSnapshot(pin, status, enumeration, rows, groups,
            new ScanCounts(rows.Length, analyzed, unsupported, failed, canceled, outstanding));
    }
}
