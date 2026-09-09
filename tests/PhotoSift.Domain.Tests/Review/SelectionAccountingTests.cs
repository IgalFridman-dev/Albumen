using System.Collections.Immutable;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Review;

public sealed class SelectionAccountingTests
{
    [Fact]
    public void EmptySelectionHasZeroRecoveryWithoutInspectingEvidence()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: null,
            linkCount: null,
            stable: false);

        var recovery = SelectionAccounting.Calculate(
            group,
            ReviewGroups.Ids(group),
            ImmutableHashSet<long>.Empty);

        Assert.Equal(new SelectionRecovery(0, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void SelectingOnlySomeKnownAliasesHasZeroRecoveryWithoutInspectingEvidence()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\alias.jpg"],
            allocatedBytes: null,
            linkCount: null,
            stable: false);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(new SelectionRecovery(0, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void AnUnavailableKnownAliasMakesSelectedPhysicalRecoveryZero()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\unavailable-alias.jpg"],
            allocatedBytes: 4096,
            linkCount: 1,
            stable: true);
        var eligible = ImmutableHashSet.Create(1L);

        var recovery = SelectionAccounting.Calculate(group, eligible, [1]);

        Assert.Equal(new SelectionRecovery(0, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void SelectingOneDuplicateCaseEquivalentRowSelectsItsSingleKnownPath()
    {
        var key = ReviewGroups.Key(1);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, key, @"C:\scope\a.jpg", 4096, 1),
            ReviewGroups.Occurrence(2, key, @"c:\SCOPE\A.JPG", 4096, 1));

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(new SelectionRecovery(4096, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void StableExternalLinkMakesRecoveryZero()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: 4096,
            linkCount: 2,
            stable: true);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(new SelectionRecovery(0, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void StableExternalLinkMakesRecoveryZeroWithoutAllocationEvidence()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: null,
            linkCount: 2,
            stable: true);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(new SelectionRecovery(0, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void FullySelectedPhysicalAllocationsCountExactlyOncePerIdentity()
    {
        var first = ReviewGroups.Key(1);
        var second = ReviewGroups.Key(2);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, first, @"C:\scope\a.jpg", 4096, 2),
            ReviewGroups.Occurrence(2, first, @"C:\scope\alias.jpg", 4096, 2),
            ReviewGroups.Occurrence(3, second, @"C:\scope\copy.jpg", 2048, 1));
        var eligible = ReviewGroups.Ids(group);

        var recovery = SelectionAccounting.Calculate(group, eligible, eligible);

        Assert.Equal(new SelectionRecovery(6144, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void PartialAndFullySelectedPhysicalKeysAreAccountedIndependently()
    {
        var first = ReviewGroups.Key(1);
        var second = ReviewGroups.Key(2);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, first, @"C:\scope\a.jpg", 4096, 2),
            ReviewGroups.Occurrence(2, first, @"C:\scope\alias.jpg", 4096, 2),
            ReviewGroups.Occurrence(3, second, @"C:\scope\copy.jpg", 2048, 1));

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1, 3]);

        Assert.Equal(new SelectionRecovery(2048, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void SelectionRecoveryNeverReadsCandidatePotentialRecovery()
    {
        var potential = new PotentialRecovery(null, RecoveryUnavailableReason.Overflow);
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: 4096,
            linkCount: 1,
            stable: true,
            potential);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(new SelectionRecovery(4096, SelectionRecoveryUnavailableReason.None), recovery);
    }

    [Fact]
    public void MissingAllocationForASelectedLastPathIsTypedUnavailable()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: null,
            linkCount: 1,
            stable: true);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.UnknownAllocation),
            recovery);
    }

    [Fact]
    public void MissingLinkCountForASelectedLastPathIsTypedUnavailable()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: 4096,
            linkCount: null,
            stable: true);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.UnknownLinks),
            recovery);
    }

    [Fact]
    public void UnstableFactsForASelectedLastPathAreTypedInconsistent()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg"],
            allocatedBytes: 4096,
            linkCount: 1,
            stable: false);

        var recovery = SelectionAccounting.Calculate(group, ReviewGroups.Ids(group), [1]);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.InconsistentFacts),
            recovery);
    }

    [Fact]
    public void ContradictoryAllocationOrLinkFactsAreTypedInconsistent()
    {
        var key = ReviewGroups.Key(1);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, key, @"C:\scope\a.jpg", 4096, 2),
            ReviewGroups.Occurrence(2, key, @"C:\scope\alias.jpg", 8192, 3));
        var eligible = ReviewGroups.Ids(group);

        var recovery = SelectionAccounting.Calculate(group, eligible, eligible);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.InconsistentFacts),
            recovery);
    }

    [Fact]
    public void LinkCountBelowDistinctKnownPathsIsTypedInconsistent()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\alias.jpg"],
            allocatedBytes: 4096,
            linkCount: 1,
            stable: true);
        var eligible = ReviewGroups.Ids(group);

        var recovery = SelectionAccounting.Calculate(group, eligible, eligible);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.InconsistentFacts),
            recovery);
    }

    [Fact]
    public void ContradictoryLinkCoveragePrecedesMissingAllocation()
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\alias.jpg"],
            allocatedBytes: null,
            linkCount: 1,
            stable: true);
        var eligible = ReviewGroups.Ids(group);

        var recovery = SelectionAccounting.Calculate(group, eligible, eligible);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.InconsistentFacts),
            recovery);
    }

    [Fact]
    public void KnownLinkCoverageBelowTwoSelectedPathsPrecedesUnknownLinks()
    {
        var key = ReviewGroups.Key(1);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, key, @"C:\scope\a.jpg", 4096, 1),
            ReviewGroups.Occurrence(2, key, @"C:\scope\alias.jpg", 4096, null));
        var eligible = ReviewGroups.Ids(group);

        var recovery = SelectionAccounting.Calculate(group, eligible, eligible);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.InconsistentFacts),
            recovery);
    }

    [Fact]
    public void CheckedAllocationArithmeticReportsOverflow()
    {
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, ReviewGroups.Key(1), @"C:\scope\a.jpg", long.MaxValue, 1),
            ReviewGroups.Occurrence(2, ReviewGroups.Key(2), @"C:\scope\b.jpg", 1, 1));
        var eligible = ReviewGroups.Ids(group);

        var recovery = SelectionAccounting.Calculate(group, eligible, eligible);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.Overflow),
            recovery);
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(2, false)]
    public void SelectedOccurrenceMustBeCurrentAndEligible(long selectedId, bool includeInEligible)
    {
        var group = ReviewGroups.OnePhysicalKey(
            [@"C:\scope\a.jpg", @"C:\scope\b.jpg"],
            allocatedBytes: 4096,
            linkCount: 2,
            stable: true);
        var eligible = includeInEligible
            ? ReviewGroups.Ids(group).Add(selectedId)
            : ImmutableHashSet.Create(1L);

        var recovery = SelectionAccounting.Calculate(group, eligible, [selectedId]);

        Assert.Equal(
            new SelectionRecovery(null, SelectionRecoveryUnavailableReason.InconsistentFacts),
            recovery);
    }
}
