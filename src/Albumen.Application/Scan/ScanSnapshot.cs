using System.Collections.Immutable;
using Albumen.Application.Semantic;
using Albumen.Application.Shell;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

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
    long Unsupported,
    long Failed,
    long Canceled,
    long Outstanding);

public sealed record ScanSnapshot(
    ScanPin? Pin,
    ScanStatus Status,
    StageOutcome Enumeration,
    ImmutableArray<ScanFileOccurrence> Occurrences,
    ImmutableArray<CandidateGroup> Groups,
    ScanCounts Counts)
{
    public bool HasUnsettledWork { get; init; }

    // Reported beside the Scan, never inside it: no semantic state changes a row, group or count.
    public SemanticSnapshot Semantic { get; init; } = SemanticSnapshot.Idle;
}

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
