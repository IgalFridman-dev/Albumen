using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Domain.Tests.Scope;

public sealed class ScopePreflightPolicyTests
{
    [Theory]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Local, ScopeTraversalState.Complete, true)]
    [InlineData(ScopeVolumeKind.Removable, ScopeCloudState.Local, ScopeTraversalState.Complete, true)]
    [InlineData(ScopeVolumeKind.Remote, ScopeCloudState.Local, ScopeTraversalState.Complete, false)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Synchronized, ScopeTraversalState.Complete, false)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Placeholder, ScopeTraversalState.Complete, false)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Unknown, ScopeTraversalState.Complete, false)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Local, ScopeTraversalState.DescendantAccessDenied, false)]
    public void EvaluateFailsClosed(
        ScopeVolumeKind volume,
        ScopeCloudState cloud,
        ScopeTraversalState traversal,
        bool ready)
    {
        var result = ScopePreflightPolicy.Evaluate(Evidence(volume, cloud, traversal));

        Assert.Equal(ready, result.Decision is ScopePreflightDecision.Ready);
    }

    [Theory]
    [InlineData(ScopeVolumeKind.Unavailable, ScopeBlockingReason.UnavailableRoot)]
    [InlineData(ScopeVolumeKind.Remote, ScopeBlockingReason.NonLocalRoot)]
    [InlineData(ScopeVolumeKind.Unsupported, ScopeBlockingReason.UnsupportedRoot)]
    [InlineData(ScopeVolumeKind.Unknown, ScopeBlockingReason.UnknownSafety)]
    public void EvaluateMapsUnsafeVolumesToTheirBlockers(
        ScopeVolumeKind volume,
        ScopeBlockingReason expectedBlocker)
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(volume, ScopeCloudState.Local, ScopeTraversalState.Complete));

        Assert.Equal(new[] { expectedBlocker }, result.BlockingReasons);
    }

    [Theory]
    [InlineData(ScopeCloudState.Synchronized, ScopeBlockingReason.SynchronizedLocation)]
    [InlineData(ScopeCloudState.Placeholder, ScopeBlockingReason.PlaceholderLocation)]
    [InlineData(ScopeCloudState.Unknown, ScopeBlockingReason.UnknownSafety)]
    public void EvaluateMapsUnsafeCloudStatesToTheirBlockers(
        ScopeCloudState cloud,
        ScopeBlockingReason expectedBlocker)
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Fixed, cloud, ScopeTraversalState.Complete));

        Assert.Equal(new[] { expectedBlocker }, result.BlockingReasons);
    }

    [Theory]
    [InlineData(ScopeTraversalState.RootAccessDenied, ScopeBlockingReason.RootAccessDenied)]
    [InlineData(ScopeTraversalState.DescendantAccessDenied, ScopeBlockingReason.DescendantAccessDenied)]
    [InlineData(ScopeTraversalState.Unknown, ScopeBlockingReason.UnknownSafety)]
    public void EvaluateMapsUnsafeTraversalStatesToTheirBlockers(
        ScopeTraversalState traversal,
        ScopeBlockingReason expectedBlocker)
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Fixed, ScopeCloudState.Local, traversal));

        Assert.Equal(new[] { expectedBlocker }, result.BlockingReasons);
    }

    [Fact]
    public void EvaluateMarksNotRunTraversalAsUnknownSafetyWhenNoOtherBlockerExists()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Fixed, ScopeCloudState.Local, ScopeTraversalState.NotRun));

        Assert.Equal(
            new[] { ScopeBlockingReason.UnknownSafety },
            result.BlockingReasons);
    }

    [Fact]
    public void EvaluateOrdersAndDeduplicatesBlockersByTheClosedReasonOrder()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Remote, ScopeCloudState.Placeholder, ScopeTraversalState.RootAccessDenied));

        Assert.Equal(
            new[]
            {
                ScopeBlockingReason.NonLocalRoot,
                ScopeBlockingReason.PlaceholderLocation,
                ScopeBlockingReason.RootAccessDenied,
            },
            result.BlockingReasons);
    }

    [Fact]
    public void EvaluateDeduplicatesUnknownSafetyBlockers()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Unknown, ScopeCloudState.Unknown, ScopeTraversalState.Unknown));

        Assert.Equal(
            new[] { ScopeBlockingReason.UnknownSafety },
            result.BlockingReasons);
    }

    [Fact]
    public void EvaluateFailsClosedForAnUnrecognizedEnumValue()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence((ScopeVolumeKind)999, ScopeCloudState.Local, ScopeTraversalState.Complete));

        Assert.Equal(ScopePreflightDecision.Blocked, result.Decision);
        Assert.Equal(
            new[] { ScopeBlockingReason.UnknownSafety },
            result.BlockingReasons);
    }

    [Fact]
    public void EvaluateRequiresAResolvedScopeForReady()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(
                ScopeVolumeKind.Fixed,
                ScopeCloudState.Local,
                ScopeTraversalState.Complete,
                includeScope: false));

        Assert.Equal(ScopePreflightDecision.Blocked, result.Decision);
        Assert.Null(result.CompleteCounts);
        Assert.Empty(result.BlockingReasons);
    }

    [Fact]
    public void EvaluateKeepsUnreadableAndCodecUnavailableFilesVisibleWithoutBlockingACompleteScope()
    {
        var counts = new ScopeOutcomeCounts(1, 2, 3, 4);

        var result = ScopePreflightPolicy.Evaluate(
            Evidence(
                ScopeVolumeKind.Fixed,
                ScopeCloudState.Local,
                ScopeTraversalState.Complete,
                counts: counts));

        Assert.Equal(ScopePreflightDecision.Ready, result.Decision);
        Assert.Same(counts, result.CompleteCounts);
    }

    [Fact]
    public void EvaluateAllowsACompleteScopeWithNoRecognizedImages()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Fixed, ScopeCloudState.Local, ScopeTraversalState.Complete));

        Assert.Equal(ScopePreflightDecision.Ready, result.Decision);
        Assert.Equal(0, result.CompleteCounts!.Supported);
    }

    [Fact]
    public void EvaluateKeepsTraversalRestrictionsVisibleOnReadyScopes()
    {
        var restrictions = new ScopeRestrictionCounts(5, 6);

        var result = ScopePreflightPolicy.Evaluate(
            Evidence(
                ScopeVolumeKind.Fixed,
                ScopeCloudState.Local,
                ScopeTraversalState.Complete,
                restrictions: restrictions));

        Assert.Equal(ScopePreflightDecision.Ready, result.Decision);
        Assert.Same(restrictions, result.Restrictions);
    }

    [Fact]
    public void EvaluateDoesNotExposeCompleteCountsWhenBlocked()
    {
        var result = ScopePreflightPolicy.Evaluate(
            Evidence(ScopeVolumeKind.Remote, ScopeCloudState.Local, ScopeTraversalState.Complete));

        Assert.Equal(ScopePreflightDecision.Blocked, result.Decision);
        Assert.Null(result.CompleteCounts);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void ScopeOutcomeCountsRejectsNegativeValues(
        int supported,
        int unsupportedExtension,
        int unreadable,
        int codecUnavailable)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScopeOutcomeCounts(supported, unsupportedExtension, unreadable, codecUnavailable));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ScopeRestrictionCountsRejectsNegativeValues(
        int escapingReparseTargets,
        int duplicateDirectoryIdentities)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScopeRestrictionCounts(escapingReparseTargets, duplicateDirectoryIdentities));
    }

    private static ScopePreflightEvidence Evidence(
        ScopeVolumeKind volume,
        ScopeCloudState cloud,
        ScopeTraversalState traversal,
        bool includeScope = true,
        ResolvedScope? scope = null,
        ScopeOutcomeCounts? counts = null,
        ScopeRestrictionCounts? restrictions = null) =>
        new(
            includeScope
                ? scope ?? new ResolvedScope(
                    new ScopeSelection(@"C:\Photos", ScopeRecursion.IncludeSubfolders),
                    @"C:\Photos",
                    new DirectoryIdentity(1, 2, 3))
                : null,
            volume,
            cloud,
            traversal,
            counts ?? new ScopeOutcomeCounts(0, 0, 0, 0),
            restrictions ?? new ScopeRestrictionCounts(0, 0));
}
