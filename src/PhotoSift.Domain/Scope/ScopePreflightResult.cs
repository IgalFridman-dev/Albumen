using System.Collections.Immutable;

namespace PhotoSift.Domain.Scope;

public enum ScopePreflightDecision
{
    Ready,
    Blocked,
}

public enum ScopeBlockingReason
{
    UnavailableRoot,
    NonLocalRoot,
    UnsupportedRoot,
    SynchronizedLocation,
    PlaceholderLocation,
    RootAccessDenied,
    DescendantAccessDenied,
    UnknownSafety,
}

public sealed record ScopePreflightResult(
    ScopePreflightDecision Decision,
    ResolvedScope? Scope,
    ScopeOutcomeCounts? CompleteCounts,
    ScopeRestrictionCounts Restrictions,
    ImmutableArray<ScopeBlockingReason> BlockingReasons);
