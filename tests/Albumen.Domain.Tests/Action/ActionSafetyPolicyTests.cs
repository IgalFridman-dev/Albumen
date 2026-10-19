using System.Collections.Immutable;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Action;

public sealed class ActionSafetyPolicyTests
{
    [Fact]
    public void OrdinaryCurrentSurvivorAllowsTheRemainingRemoval()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg", @"C:\scope\b.jpg"], linkCount: 2);
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.Allowed, result.Kind);
        Assert.Equal(ActionSafetyReason.None, result.Reason);
        Assert.Equal(SurvivorDecisionKind.Allowed, result.Survivor.Kind);
        Assert.Equal(1, result.Survivor.RemainingKnownPathOccurrences);
    }

    [Fact]
    public void ExactOverrideAllowsTheDeliberateFinalSurvivorRemoval()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var input = ActionGroups.Input(
            approved,
            approved.Members,
            remainingApproved: [1],
            overrideApproved: [1],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.Allowed, result.Kind);
        Assert.Equal(ActionSafetyReason.None, result.Reason);
        Assert.Equal(SurvivorDecisionKind.OverrideRequired, result.Survivor.Kind);
    }

    [Fact]
    public void ExactOverrideMustCoverEveryRemainingApprovedOccurrence()
    {
        var approved = ActionGroups.TwoKeys(linkCount: 1);
        var input = ActionGroups.Input(
            approved,
            approved.Members,
            remainingApproved: [1, 2],
            overrideApproved: [1],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, result.Reason);
    }

    [Fact]
    public void RemainingSubsetAfterPriorSuccessUsesOnlyCurrentOccurrences()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg", @"C:\scope\b.jpg"], linkCount: 2);
        var current = ImmutableArray.Create(ActionGroups.Refresh(approved.Members[1], linkCount: 1));
        var input = ActionGroups.Input(
            approved,
            current,
            remainingApproved: [2],
            overrideApproved: [2],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.Allowed, result.Kind);
        Assert.Equal(SurvivorDecisionKind.OverrideRequired, result.Survivor.Kind);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(2u, false)]
    [InlineData(0u, true)]
    public void ExactOverrideCannotCureUnknownUnstableOrZeroLinkEvidence(uint? linkCount, bool stable)
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var current = ImmutableArray.Create(
            ActionGroups.RefreshFacts(
                approved.Members[0],
                approved.Members[0].Allocation.AllocatedBytes,
                linkCount,
                stable));
        var input = ActionGroups.Input(
            approved,
            current,
            remainingApproved: [1],
            overrideApproved: [1],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void ZeroAllocationCannotReachTheOrdinarySurvivorAllowBranch()
    {
        var approved = ActionGroups.OneKey(
            [@"C:\scope\selected.jpg", @"C:\scope\witness.jpg"],
            linkCount: 2,
            allocatedBytes: 0);
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void ZeroAllocationCannotReachTheExactOverrideAllowBranch()
    {
        var approved = ActionGroups.OneKey(
            [@"C:\scope\selected.jpg"],
            linkCount: 1,
            allocatedBytes: 0);
        var input = ActionGroups.Input(
            approved,
            approved.Members,
            remainingApproved: [1],
            overrideApproved: [1],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void MissingCurrentRemainingOccurrenceFailsClosed()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var input = ActionGroups.Input(
            approved,
            ImmutableArray<ScanFileOccurrence>.Empty,
            remainingApproved: [1],
            overrideApproved: [1],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.Missing, result.Reason);
    }

    [Fact]
    public void DuplicateCurrentOccurrenceIdsFailClosed()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var current = ImmutableArray.Create(approved.Members[0], approved.Members[0]);
        var input = ActionGroups.Input(approved, current, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void CaseEquivalentCurrentPathsCountAsOneKnownPath()
    {
        var key = ActionGroups.Key(1);
        var approved = ActionGroups.Group(
            ActionGroups.Occurrence(1, key, @"C:\scope\a.jpg", linkCount: 2),
            ActionGroups.Occurrence(2, key, @"c:\SCOPE\A.JPG", linkCount: 2));
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1, 2]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.Allowed, result.Kind);
        var survivor = Assert.Single(result.Survivor.ExternalSurvivors);
        Assert.Equal(1, survivor.KnownPathCount);
        Assert.Equal((uint)2, survivor.LinkCount);
    }

    [Fact]
    public void OneCanonicalCurrentPathCannotClaimTwoPhysicalIdentities()
    {
        var approved = ActionGroups.Group(
            ActionGroups.Occurrence(1, ActionGroups.Key(1), @"C:\scope\a.jpg", linkCount: 1),
            ActionGroups.Occurrence(2, ActionGroups.Key(2), @"c:\SCOPE\A.JPG", linkCount: 1));
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void CurrentOccurrenceWithAnotherScanPinHasStaleAuthority()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var stalePin = approved.Pin with { AnalysisRevision = approved.Pin.AnalysisRevision + 1 };
        var current = ImmutableArray.Create(ActionGroups.Refresh(approved.Members[0], pin: stalePin));
        var input = ActionGroups.Input(approved, current, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.StaleAuthority, result.Kind);
        Assert.Equal(ActionSafetyReason.StalePin, result.Reason);
    }

    [Fact]
    public void CurrentOccurrenceWithAnotherRevisionHasStaleAuthority()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var current = ImmutableArray.Create(
            ActionGroups.Refresh(approved.Members[0], occurrenceRevision: 2));
        var input = ActionGroups.Input(approved, current, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.StaleAuthority, result.Kind);
        Assert.Equal(ActionSafetyReason.StalePin, result.Reason);
    }

    [Fact]
    public void EmptyApprovedGroupIdentityHasStaleAuthority()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1) with
        {
            GroupId = Guid.Empty,
        };
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.StaleAuthority, result.Kind);
        Assert.Equal(ActionSafetyReason.StalePin, result.Reason);
    }

    [Fact]
    public void DependentUnknownOutcomePrecedesAnOtherwiseValidOverride()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var input = ActionGroups.Input(
            approved,
            approved.Members,
            remainingApproved: [1],
            overrideApproved: [1],
            overrideExact: true,
            hasDependentUnknown: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.DependentOutcomeUnknown, result.Reason);
    }

    [Fact]
    public void RemainingOccurrenceOutsideTheApprovedGroupHasStaleAuthority()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [99]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.StaleAuthority, result.Kind);
        Assert.Equal(ActionSafetyReason.StalePin, result.Reason);
    }

    [Fact]
    public void FreshEligibleOccurrenceOutsideTheApprovedGroupHasStaleAuthority()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var input = ActionGroups.Input(
            approved,
            approved.Members,
            remainingApproved: [1],
            freshEligible: [1, 99]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.StaleAuthority, result.Kind);
        Assert.Equal(ActionSafetyReason.StalePin, result.Reason);
    }

    [Fact]
    public void ChangedCurrentPhysicalAndContentRevisionFailsClosed()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var current = ImmutableArray.Create(
            ActionGroups.Occurrence(1, ActionGroups.Key(9), @"C:\scope\a.jpg", linkCount: 1));
        var input = ActionGroups.Input(approved, current, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.Changed, result.Reason);
    }

    [Fact]
    public void ChangedCurrentCanonicalPathFailsClosed()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var current = ImmutableArray.Create(
            ActionGroups.Refresh(approved.Members[0], path: @"C:\scope\renamed.jpg"));
        var input = ActionGroups.Input(approved, current, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.Changed, result.Reason);
    }

    [Fact]
    public void InconsistentSameKeyAllocationPrecedesAKnownWitness()
    {
        var key = ActionGroups.Key(1);
        var approved = ActionGroups.Group(
            ActionGroups.Occurrence(1, key, @"C:\scope\a.jpg", allocatedBytes: 4096, linkCount: 2),
            ActionGroups.Occurrence(2, key, @"C:\scope\b.jpg", allocatedBytes: 8192, linkCount: 2));
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void InconsistentSameKeyLinkCountPrecedesAKnownWitness()
    {
        var key = ActionGroups.Key(1);
        var approved = ActionGroups.Group(
            ActionGroups.Occurrence(1, key, @"C:\scope\a.jpg", allocatedBytes: 4096, linkCount: 2),
            ActionGroups.Occurrence(2, key, @"C:\scope\b.jpg", allocatedBytes: 4096, linkCount: 3));
        var input = ActionGroups.Input(approved, approved.Members, remainingApproved: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void MissingOrNegativeAllocationFailsBeforeEitherAllowBranch(long? allocatedBytes)
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var current = ImmutableArray.Create(
            ActionGroups.RefreshFacts(approved.Members[0], allocatedBytes, linkCount: 1, stable: true));
        var input = ActionGroups.Input(
            approved,
            current,
            remainingApproved: [1],
            overrideApproved: [1],
            overrideExact: true);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.InconsistentLinks, result.Reason);
    }

    [Fact]
    public void MissingCurrentWitnessFailsBeforeAnOrdinarySurvivorCanBeClaimed()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\selected.jpg", @"C:\scope\witness.jpg"], linkCount: 2);
        var missingWitness = ActionGroups.FailedRefresh(approved.Members[1], ScanReason.Missing);
        var current = ImmutableArray.Create(approved.Members[0], missingWitness);
        var input = ActionGroups.Input(approved, current, remainingApproved: [1], freshEligible: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.Missing, result.Reason);
    }

    [Fact]
    public void ChangedCurrentWitnessFailsBeforeAnOrdinarySurvivorCanBeClaimed()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\selected.jpg", @"C:\scope\witness.jpg"], linkCount: 2);
        var changedWitness = ActionGroups.FailedRefresh(approved.Members[1], ScanReason.Changed);
        var current = ImmutableArray.Create(approved.Members[0], changedWitness);
        var input = ActionGroups.Input(approved, current, remainingApproved: [1], freshEligible: [1]);

        var result = ActionSafetyPolicy.Evaluate(input);

        Assert.Equal(ActionSafetyDecisionKind.UnsafeCurrentFacts, result.Kind);
        Assert.Equal(ActionSafetyReason.Changed, result.Reason);
    }

    [Fact]
    public void DefaultCurrentCollectionIsAProgrammingContractViolation()
    {
        var approved = ActionGroups.OneKey([@"C:\scope\a.jpg"], linkCount: 1);
        var input = ActionGroups.Input(approved, default, remainingApproved: [1]);

        Assert.Throws<ArgumentException>(() => ActionSafetyPolicy.Evaluate(input));
    }

    [Fact]
    public void NullInputIsAProgrammingContractViolation()
    {
        Assert.Throws<ArgumentNullException>(() => ActionSafetyPolicy.Evaluate(null!));
    }
}

internal static class ActionGroups
{
    private const string Sha256A = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
    private static readonly ScanPin PinValue = new(
        new Guid("11111111-1111-1111-1111-111111111111"),
        ScopeRevision: 2,
        RuleSetRevision: 3,
        AnalysisRevision: 4);

    internal static PhysicalFileKey Key(ulong low) => new(7, new FileId128(0, low));

    internal static CandidateGroup OneKey(
        IEnumerable<string> paths,
        uint? linkCount,
        long? allocatedBytes = 4096,
        bool stable = true)
    {
        var key = Key(1);
        return Group([.. paths.Select((path, index) =>
            Occurrence(index + 1, key, path, allocatedBytes, linkCount, stable))]);
    }

    internal static CandidateGroup TwoKeys(uint? linkCount) => Group(
        Occurrence(1, Key(1), @"C:\scope\a.jpg", linkCount: linkCount),
        Occurrence(2, Key(2), @"C:\scope\b.jpg", linkCount: linkCount));

    internal static CandidateGroup Group(params ScanFileOccurrence[] members) => new(
        new Guid("22222222-2222-2222-2222-222222222222"),
        GroupRevision: 1,
        PinValue,
        CandidateCategory.Exact,
        [.. members],
        ImmutableArray<EqualityEvidence>.Empty,
        Uncertain: false,
        KeeperOccurrenceId: null,
        new PotentialRecovery(123, RecoveryUnavailableReason.None));

    internal static ScanFileOccurrence Occurrence(
        long id,
        PhysicalFileKey key,
        string path,
        long? allocatedBytes = 4096,
        uint? linkCount = 1,
        bool stable = true,
        ScanPin? pin = null,
        long occurrenceRevision = 1,
        long attempt = 1,
        string sha256 = Sha256A)
    {
        var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        return new ScanFileOccurrence(
            new OccurrenceToken(pin ?? PinValue, id, occurrenceRevision, attempt),
            path,
            path,
            key,
            succeeded,
            succeeded,
            new ContentRevision(key, byteLength: 3, sha256),
            new AllocationEvidence(allocatedBytes, linkCount, stable));
    }

    internal static ScanFileOccurrence Refresh(
        ScanFileOccurrence approved,
        uint? linkCount = null,
        bool? stable = null,
        long? allocatedBytes = null,
        ScanPin? pin = null,
        long? occurrenceRevision = null,
        string? path = null) =>
        Occurrence(
            approved.Token.OccurrenceId,
            approved.PhysicalFileKey!.Value,
            path ?? approved.ResolvedPath,
            allocatedBytes ?? approved.Allocation.AllocatedBytes,
            linkCount ?? approved.Allocation.LinkCount,
            stable ?? approved.Allocation.Stable,
            pin ?? approved.Token.Pin,
            occurrenceRevision ?? approved.Token.OccurrenceRevision,
            approved.Token.Attempt,
            approved.Content!.Sha256);

    internal static ScanFileOccurrence RefreshFacts(
        ScanFileOccurrence approved,
        long? allocatedBytes,
        uint? linkCount,
        bool stable) =>
        Occurrence(
            approved.Token.OccurrenceId,
            approved.PhysicalFileKey!.Value,
            approved.ResolvedPath,
            allocatedBytes,
            linkCount,
            stable,
            approved.Token.Pin,
            approved.Token.OccurrenceRevision,
            approved.Token.Attempt,
            approved.Content!.Sha256);

    internal static ScanFileOccurrence FailedRefresh(
        ScanFileOccurrence approved,
        ScanReason reason)
    {
        var failed = new StageOutcome(ScanStageState.FailedRetryable, reason);
        return new ScanFileOccurrence(
            approved.Token,
            approved.DiscoveredPath,
            approved.ResolvedPath,
            physicalFileKey: null,
            approved.Enumerate,
            failed,
            content: null,
            new AllocationEvidence(null, null, false));
    }

    internal static ActionSafetyInput Input(
        CandidateGroup approved,
        ImmutableArray<ScanFileOccurrence> current,
        IEnumerable<long> remainingApproved,
        IEnumerable<long>? overrideApproved = null,
        bool overrideExact = false,
        bool hasDependentUnknown = false,
        IEnumerable<long>? freshEligible = null)
    {
        var eligible = freshEligible is not null
            ? freshEligible.ToImmutableHashSet()
            : current.IsDefault
                ? ImmutableHashSet<long>.Empty
                : current.Select(item => item.Token.OccurrenceId).ToImmutableHashSet();
        return new ActionSafetyInput(
            approved,
            current,
            eligible,
            remainingApproved.ToImmutableHashSet(),
            (overrideApproved ?? []).ToImmutableHashSet(),
            overrideExact,
            hasDependentUnknown);
    }
}
