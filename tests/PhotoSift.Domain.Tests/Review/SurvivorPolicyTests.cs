using System.Collections.Immutable;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Review;

public sealed class SurvivorPolicyTests
{
    [Fact]
    public void SelectingEveryKnownAliasUsesAStableExternalLinkAsTheOnlySurvivor()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"c:\SCOPE\b.jpg"],
            allocatedBytes: 4096,
            linkCount: 3,
            stable: true);
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, eligible);

        Assert.Equal(SurvivorDecisionKind.Allowed, decision.Kind);
        Assert.Equal(SurvivorUnavailableReason.None, decision.Reason);
        Assert.Equal(0, decision.RemainingKnownPathOccurrences);
        Assert.Equal(1, decision.RemainingPhysicalKeys);
        var survivor = Assert.Single(decision.ExternalSurvivors);
        Assert.Equal((uint)3, survivor.LinkCount);
        Assert.Equal(2, survivor.KnownPathCount);
    }

    [Fact]
    public void AnUnselectedCurrentPathAllowsSelectionWithoutAllocationOrLinkEvidence()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\b.jpg"],
            allocatedBytes: null,
            linkCount: null,
            stable: false);
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, [1]);

        Assert.Equal(SurvivorDecisionKind.Allowed, decision.Kind);
        Assert.Equal(1, decision.RemainingKnownPathOccurrences);
        Assert.Equal(1, decision.RemainingPhysicalKeys);
        Assert.Empty(decision.ExternalSurvivors);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(2u, false)]
    public void UnknownLinkEvidenceCannotProveASelectedLastPath(uint? linkCount, bool stable)
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: 4096,
            linkCount,
            stable);
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, eligible);

        Assert.Equal(SurvivorDecisionKind.OverrideRequired, decision.Kind);
        Assert.Equal(SurvivorUnavailableReason.UnknownLinks, decision.Reason);
        Assert.Equal(0, decision.RemainingKnownPathOccurrences);
        Assert.Equal(0, decision.RemainingPhysicalKeys);
        Assert.Empty(decision.ExternalSurvivors);
    }

    [Fact]
    public void SelectingEveryKnownPathWithoutAnExternalLinkRequiresOverride()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\b.jpg"],
            allocatedBytes: 4096,
            linkCount: 2,
            stable: true);
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, eligible);

        Assert.Equal(SurvivorDecisionKind.OverrideRequired, decision.Kind);
        Assert.Equal(SurvivorUnavailableReason.NoProvenSurvivor, decision.Reason);
        Assert.Equal(0, decision.RemainingPhysicalKeys);
    }

    [Fact]
    public void ALinkCountBelowDistinctKnownPathsIsInconsistentBeforeOverride()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\b.jpg"],
            allocatedBytes: 4096,
            linkCount: 1,
            stable: true);
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, eligible);

        Assert.Equal(SurvivorDecisionKind.InconsistentEvidence, decision.Kind);
        Assert.Equal(SurvivorUnavailableReason.InconsistentFacts, decision.Reason);
    }

    [Fact]
    public void ContradictoryStableLinkCountsAreInconsistentBeforeOverride()
    {
        var key = ReviewGroups.Key(1);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, key, @"C:\scope\a.jpg", 4096, 2),
            ReviewGroups.Occurrence(2, key, @"C:\scope\b.jpg", 4096, 3));
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, eligible);

        Assert.Equal(SurvivorDecisionKind.InconsistentEvidence, decision.Kind);
        Assert.Equal(SurvivorUnavailableReason.InconsistentFacts, decision.Reason);
    }

    [Fact]
    public void AProvenKnownPathDoesNotDependOnContradictoryLinkEvidence()
    {
        var key = ReviewGroups.Key(1);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, key, @"C:\scope\a.jpg", 4096, 1),
            ReviewGroups.Occurrence(2, key, @"C:\scope\b.jpg", 4096, 3));
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, [1]);

        Assert.Equal(SurvivorDecisionKind.Allowed, decision.Kind);
        Assert.Equal(1, decision.RemainingKnownPathOccurrences);
        Assert.Equal(1, decision.RemainingPhysicalKeys);
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(2, false)]
    public void ProposedOccurrenceMustBeCurrentAndEligible(long proposedId, bool includeInEligible)
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\b.jpg"],
            allocatedBytes: 4096,
            linkCount: 2,
            stable: true);
        var eligible = includeInEligible
            ? ReviewGroups.Ids(group).Add(proposedId)
            : ImmutableHashSet.Create(1L);

        var decision = SurvivorPolicy.Evaluate(group, eligible, [proposedId]);

        Assert.Equal(SurvivorDecisionKind.IneligibleSelection, decision.Kind);
        Assert.Equal(SurvivorUnavailableReason.IneligibleOccurrence, decision.Reason);
    }

    [Fact]
    public void DuplicateCaseEquivalentRowsCountAsOneKnownPath()
    {
        var key = ReviewGroups.Key(1);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, key, @"C:\scope\a.jpg", 4096, 2),
            ReviewGroups.Occurrence(2, key, @"c:\SCOPE\A.JPG", 4096, 2),
            ReviewGroups.Occurrence(3, key, @"C:\scope\b.jpg", 4096, 2));
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, [1]);

        Assert.Equal(SurvivorDecisionKind.Allowed, decision.Kind);
        Assert.Equal(1, decision.RemainingKnownPathOccurrences);
        Assert.Equal(1, decision.RemainingPhysicalKeys);
    }

    [Fact]
    public void RemainingPhysicalKeysCountKnownAndExternalSurvivorsOncePerIdentity()
    {
        var first = ReviewGroups.Key(1);
        var second = ReviewGroups.Key(2);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, first, @"C:\scope\a.jpg", 4096, 2),
            ReviewGroups.Occurrence(2, first, @"C:\scope\b.jpg", 4096, 2),
            ReviewGroups.Occurrence(3, second, @"C:\scope\copy.jpg", 4096, 2));
        var eligible = ReviewGroups.Ids(group);

        var decision = SurvivorPolicy.Evaluate(group, eligible, [1, 3]);

        Assert.Equal(SurvivorDecisionKind.Allowed, decision.Kind);
        Assert.Equal(1, decision.RemainingKnownPathOccurrences);
        Assert.Equal(2, decision.RemainingPhysicalKeys);
        Assert.Equal(second, Assert.Single(decision.ExternalSurvivors).PhysicalFileKey);
    }
}

internal static class ReviewGroups
{
    private const string Sha256 = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
    private static readonly ScanPin Pin = new(
        new Guid("11111111-1111-1111-1111-111111111111"),
        ScopeRevision: 2,
        RuleSetRevision: 3,
        AnalysisRevision: 4);

    internal static CandidateGroup OnePhysicalKey(
        IEnumerable<string> paths,
        long? allocatedBytes,
        uint? linkCount,
        bool stable,
        PotentialRecovery? potentialRecovery = null)
    {
        var key = Key(1);
        var members = paths.Select(
            (path, index) => Occurrence(index + 1, key, path, allocatedBytes, linkCount, stable));

        return Group(potentialRecovery, [.. members]);
    }

    internal static CandidateGroup Group(params ScanFileOccurrence[] members) =>
        Group(potentialRecovery: null, members);

    internal static CandidateGroup Group(
        PotentialRecovery? potentialRecovery,
        params ScanFileOccurrence[] members) =>
        new(
            new Guid("22222222-2222-2222-2222-222222222222"),
            GroupRevision: 1,
            Pin,
            CandidateCategory.Exact,
            [.. members],
            ImmutableArray<EqualityEvidence>.Empty,
            Uncertain: false,
            KeeperOccurrenceId: null,
            potentialRecovery ?? new PotentialRecovery(123, RecoveryUnavailableReason.None));

    internal static ScanFileOccurrence Occurrence(
        long id,
        PhysicalFileKey key,
        string path,
        long? allocatedBytes,
        uint? linkCount,
        bool stable = true)
    {
        var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        return new ScanFileOccurrence(
            new OccurrenceToken(Pin, id, OccurrenceRevision: 1, Attempt: 1),
            path,
            path,
            key,
            succeeded,
            succeeded,
            new ContentRevision(key, byteLength: 3, sha256: Sha256),
            new AllocationEvidence(allocatedBytes, linkCount, stable));
    }

    internal static PhysicalFileKey Key(ulong low) => new(7, new FileId128(0, low));

    internal static ImmutableHashSet<long> Ids(CandidateGroup group) =>
        group.Members.Select(member => member.Token.OccurrenceId).ToImmutableHashSet();
}
