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

/// <summary>
/// Per feature kind, the photos whose stored feature was reused, the photos computed because none
/// was stored, and the photos computed because a lookup, a stored value or a write failed. A photo
/// is counted once per kind.
/// </summary>
public sealed record SignatureReuseCounts(long Hits, long Misses, long Rejects)
{
    public static SignatureReuseCounts None { get; } = new(0, 0, 0);

    public long Total => Hits + Misses + Rejects;
}

public sealed record SignatureReuse(SignatureReuseCounts Near, SignatureReuseCounts Sharpness, SignatureReuseCounts Vector)
{
    public static SignatureReuse None { get; } = new(SignatureReuseCounts.None, SignatureReuseCounts.None, SignatureReuseCounts.None);
}

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

    // Facts about the work done, beside the Scan like Semantic: reuse never changes a row or group.
    public SignatureReuse Reuse { get; init; } = SignatureReuse.None;
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
