using System.Collections.Immutable;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

public enum ScanStatus
{
    Idle,
    Checking,
    Running,
    Complete,
    Incomplete,
    Faulted,
}

public sealed record ScanCounts(
    long Discovered,
    long Analyzed,
    long Excluded,
    long Failed,
    long Canceled,
    long Outstanding);

public sealed record ScanSnapshot(
    ScanPin? Pin,
    ScanStatus Status,
    StageOutcome Enumeration,
    ImmutableArray<ScanFileOccurrence> Occurrences,
    ImmutableArray<CandidateGroup> Groups,
    ScanCounts Counts);

public interface IScanCoordinator : IAsyncDisposable
{
    ScanSnapshot Current { get; }

    event EventHandler<ScanSnapshot>? SnapshotChanged;

    Task StartAsync(
        ShellSnapshot scope,
        ExactRuleConfirmation rules,
        CancellationToken cancellationToken);

    Task InvalidateAsync(long scopeRevision, long? ruleSetRevision);

    Task CancelAsync();
}
