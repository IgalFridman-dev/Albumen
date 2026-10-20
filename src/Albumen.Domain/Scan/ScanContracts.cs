using System.Collections.Immutable;

namespace Albumen.Domain.Scan;

public sealed record ScanPin(
    Guid ScanId,
    long ScopeRevision,
    long RuleSetRevision,
    long AnalysisRevision);

public sealed record OccurrenceToken(
    ScanPin Pin,
    long OccurrenceId,
    long OccurrenceRevision,
    long Attempt);

public enum ScanStageState
{
    Pending,
    Running,
    Succeeded,
    Unsupported,
    FailedRetryable,
    FailedTerminal,
    Canceled,
    NotApplicable,
}

public enum ScanReason
{
    None,
    UnsupportedExtension,
    CodecUnavailable,
    AccessDenied,
    SharingViolation,
    Missing,
    Changed,
    UnsafeLocation,
    UnknownSafety,
    ReadFailure,
    IncompleteEnumeration,
    Canceled,
    Stale,
    ProgrammingFault,
    CapabilityUnavailable,
}

public sealed record StageOutcome
{
    public StageOutcome(ScanStageState state, ScanReason reason)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Scan stage state must be defined.");
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Scan reason must be defined.");
        }

        if (!IsValidCombination(state, reason))
        {
            throw new ArgumentException(
                $"Reason {reason} is not valid for scan stage state {state}.",
                nameof(reason));
        }

        State = state;
        Reason = reason;
    }

    public ScanStageState State { get; }

    public ScanReason Reason { get; }

    private static bool IsValidCombination(ScanStageState state, ScanReason reason) => state switch
    {
        ScanStageState.Pending or ScanStageState.Running or ScanStageState.Succeeded =>
            reason is ScanReason.None,
        ScanStageState.Unsupported =>
            reason is ScanReason.UnsupportedExtension or ScanReason.CodecUnavailable,
        ScanStageState.FailedRetryable =>
            reason is ScanReason.AccessDenied
                or ScanReason.SharingViolation
                or ScanReason.Missing
                or ScanReason.Changed
                or ScanReason.ReadFailure,
        ScanStageState.FailedTerminal =>
            reason is ScanReason.UnsafeLocation
                or ScanReason.UnknownSafety
                or ScanReason.IncompleteEnumeration
                or ScanReason.ProgrammingFault,
        ScanStageState.Canceled => reason is ScanReason.Canceled,
        ScanStageState.NotApplicable => reason is not ScanReason.None,
        _ => false,
    };
}

public sealed record AllocationEvidence(long? AllocatedBytes, uint? LinkCount, bool Stable);

public sealed record ScanFileOccurrence
{
    public ScanFileOccurrence(
        OccurrenceToken token,
        string discoveredPath,
        string resolvedPath,
        PhysicalFileKey? physicalFileKey,
        StageOutcome enumerate,
        StageOutcome exact,
        ContentRevision? content,
        AllocationEvidence allocation)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(discoveredPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        ArgumentNullException.ThrowIfNull(enumerate);
        ArgumentNullException.ThrowIfNull(exact);
        ArgumentNullException.ThrowIfNull(allocation);

        if (exact.State is ScanStageState.Succeeded
            && (physicalFileKey is null || content is null))
        {
            throw new ArgumentException(
                "A successful Exact outcome requires physical identity and content evidence.",
                nameof(exact));
        }

        if (content is not null
            && (physicalFileKey is null || content.PhysicalFileKey != physicalFileKey.Value))
        {
            throw new ArgumentException(
                "Content evidence must describe the occurrence's physical identity.",
                nameof(content));
        }

        Token = token;
        DiscoveredPath = discoveredPath;
        ResolvedPath = resolvedPath;
        PhysicalFileKey = physicalFileKey;
        Enumerate = enumerate;
        Exact = exact;
        Content = content;
        Allocation = allocation;
    }

    public OccurrenceToken Token { get; }

    public string DiscoveredPath { get; }

    public string ResolvedPath { get; }

    public PhysicalFileKey? PhysicalFileKey { get; }

    public StageOutcome Enumerate { get; }

    public StageOutcome Exact { get; }

    public ContentRevision? Content { get; }

    public AllocationEvidence Allocation { get; }

    public StageOutcome VisualFeatures { get; init; } =
        new(ScanStageState.Pending, ScanReason.None);

    public StageOutcome NearDuplicate { get; init; } =
        new(ScanStageState.Pending, ScanReason.None);

    // Fine-detail score from the visual-features pass; null when that pass did not succeed.
    // It only compares photos within one set (SharpnessComparison).
    public double? Sharpness { get; init; }
}

public enum CandidateCategory
{
    Exact,
    NearDuplicate,
    Uncertain,
}

public enum RecoveryUnavailableReason
{
    None,
    UnknownAllocation,
    UnknownLinks,
    ExternalLinks,
    InconsistentFacts,
    Overflow,
}

public sealed record PotentialRecovery(long? Bytes, RecoveryUnavailableReason Reason);

public sealed record EqualityEvidence(
    OccurrenceToken Left,
    OccurrenceToken Right,
    ContentRevision LeftContent,
    ContentRevision RightContent);

public sealed record PerceptualSignature(
    string ImplementationRevision,
    ulong DifferenceHash,
    byte AverageLuma,
    byte AverageBlue,
    byte AverageGreen,
    byte AverageRed);

public enum NearRelationshipKind
{
    NotRelated,
    Uncertain,
    Near,
}

public sealed record NearDuplicateEvidence(
    OccurrenceToken Left,
    OccurrenceToken Right,
    int HammingDistance,
    int ColorDistance,
    NearRelationshipKind Kind);

public sealed record CandidateGroup(
    Guid GroupId,
    long GroupRevision,
    ScanPin Pin,
    CandidateCategory Category,
    ImmutableArray<ScanFileOccurrence> Members,
    ImmutableArray<EqualityEvidence> Equality,
    bool Uncertain,
    long? KeeperOccurrenceId,
    PotentialRecovery PotentialRecovery)
{
    private long groupRevision = ValidateRevision(GroupRevision);

    public long GroupRevision
    {
        get => groupRevision;
        init => groupRevision = ValidateRevision(value);
    }

    public ImmutableArray<NearDuplicateEvidence> Similarity { get; init; } = [];

    private static long ValidateRevision(long revision) => revision > 0
        ? revision
        : throw new ArgumentOutOfRangeException(
            nameof(revision),
            revision,
            "Candidate group revision must be positive.");
}

public sealed record ExactRuleConfirmation
{
    public ExactRuleConfirmation(long scopeRevision, long ruleSetRevision)
        : this(scopeRevision, ruleSetRevision, ScanOptions.Default)
    {
    }

    public ExactRuleConfirmation(
        long scopeRevision,
        long ruleSetRevision,
        ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ScopeRevision = scopeRevision;
        RuleSetRevision = ruleSetRevision;
        Options = options;
    }

    public long ScopeRevision { get; init; }

    public long RuleSetRevision { get; init; }

    public ScanOptions Options { get; init; }
}
