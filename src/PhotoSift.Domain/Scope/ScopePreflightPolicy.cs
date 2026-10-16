using System.Collections.Immutable;

namespace PhotoSift.Domain.Scope;

public static class ScopePreflightPolicy
{
    public static ScopePreflightResult Evaluate(ScopePreflightEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var blockers = ImmutableArray.CreateBuilder<ScopeBlockingReason>();
        AddVolumeBlocker(evidence.Volume, blockers);
        AddCloudBlocker(evidence.Cloud, blockers);
        AddTraversalBlocker(evidence.Traversal, blockers);
        if (evidence.Traversal is ScopeTraversalState.NotRun && blockers.Count is 0)
        {
            blockers.Add(ScopeBlockingReason.UnknownSafety);
        }

        if (evidence.Scope is null)
        {
            blockers.Add(ScopeBlockingReason.UnknownSafety);
        }

        var ordered = blockers.Distinct().OrderBy(reason => (int)reason).ToImmutableArray();
        var ready = ordered.IsEmpty && evidence.Scope is not null &&
            evidence.Traversal is ScopeTraversalState.Complete;
        return new ScopePreflightResult(
            ready ? ScopePreflightDecision.Ready : ScopePreflightDecision.Blocked,
            evidence.Scope,
            evidence.Volume,
            ready ? evidence.Counts : null,
            evidence.Restrictions,
            ordered);
    }

    private static void AddVolumeBlocker(
        ScopeVolumeKind volume,
        ImmutableArray<ScopeBlockingReason>.Builder blockers)
    {
        switch (volume)
        {
            case ScopeVolumeKind.Fixed:
            case ScopeVolumeKind.Removable:
                return;
            case ScopeVolumeKind.Remote:
                blockers.Add(ScopeBlockingReason.NonLocalRoot);
                return;
            case ScopeVolumeKind.Unsupported:
                blockers.Add(ScopeBlockingReason.UnsupportedRoot);
                return;
            case ScopeVolumeKind.Unavailable:
                blockers.Add(ScopeBlockingReason.UnavailableRoot);
                return;
            case ScopeVolumeKind.Unknown:
            default:
                blockers.Add(ScopeBlockingReason.UnknownSafety);
                return;
        }
    }

    private static void AddCloudBlocker(
        ScopeCloudState cloud,
        ImmutableArray<ScopeBlockingReason>.Builder blockers)
    {
        switch (cloud)
        {
            case ScopeCloudState.Local:
                return;
            case ScopeCloudState.Synchronized:
                blockers.Add(ScopeBlockingReason.SynchronizedLocation);
                return;
            case ScopeCloudState.Placeholder:
                blockers.Add(ScopeBlockingReason.PlaceholderLocation);
                return;
            case ScopeCloudState.Unknown:
            default:
                blockers.Add(ScopeBlockingReason.UnknownSafety);
                return;
        }
    }

    private static void AddTraversalBlocker(
        ScopeTraversalState traversal,
        ImmutableArray<ScopeBlockingReason>.Builder blockers)
    {
        switch (traversal)
        {
            case ScopeTraversalState.NotRun:
            case ScopeTraversalState.Complete:
                return;
            case ScopeTraversalState.RootAccessDenied:
                blockers.Add(ScopeBlockingReason.RootAccessDenied);
                return;
            case ScopeTraversalState.DescendantAccessDenied:
                blockers.Add(ScopeBlockingReason.DescendantAccessDenied);
                return;
            case ScopeTraversalState.Unknown:
            default:
                blockers.Add(ScopeBlockingReason.UnknownSafety);
                return;
        }
    }
}
