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
            var state = EffectiveState(row);
            switch (state)
            {
                case ScanStageState.Succeeded: analyzed++; break;
                case ScanStageState.Unsupported: unsupported++; break;
                case ScanStageState.NotApplicable:
                    switch (row.Enumerate.State)
                    {
                        case ScanStageState.Unsupported: unsupported++; break;
                        case ScanStageState.Canceled: canceled++; break;
                        default: failed++; break;
                    }
                    break;
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

    private static ScanStageState EffectiveState(ScanFileOccurrence row)
    {
        if (row.Exact.State != ScanStageState.Succeeded)
            return row.Exact.State;
        if (IsUnresolved(row.VisualFeatures.State))
            return row.VisualFeatures.State;
        if (IsUnresolved(row.NearDuplicate.State))
            return row.NearDuplicate.State;
        return ScanStageState.Succeeded;
    }

    private static bool IsUnresolved(ScanStageState state) => state is
        ScanStageState.Unsupported or ScanStageState.FailedRetryable
        or ScanStageState.FailedTerminal or ScanStageState.Canceled;
}
