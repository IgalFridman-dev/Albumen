using System.Collections.Immutable;
using PhotoSift.Domain.Review;
using Xunit;

namespace PhotoSift.Domain.Tests.Review;

public sealed class E1S4AcceptanceTests
{
    [Fact]
    public void MixedAliasesExternalLinksAndKnownSurvivorsComposeOneDecisionAndRecovery()
    {
        var externalKey = ReviewGroups.Key(1);
        var aliasKey = ReviewGroups.Key(2);
        var survivorKey = ReviewGroups.Key(3);
        var group = ReviewGroups.Group(
            ReviewGroups.Occurrence(1, externalKey, @"C:\scope\external-a.jpg", 4096, 3),
            ReviewGroups.Occurrence(2, externalKey, @"C:\scope\external-b.jpg", 4096, 3),
            ReviewGroups.Occurrence(3, aliasKey, @"C:\scope\alias.jpg", 2048, 1),
            ReviewGroups.Occurrence(4, aliasKey, @"c:\SCOPE\ALIAS.JPG", 2048, 1),
            ReviewGroups.Occurrence(5, survivorKey, @"C:\scope\retained.jpg", 8192, 1))
            with { GroupRevision = 37 };
        var eligible = ReviewGroups.Ids(group);
        var selected = ImmutableHashSet.Create(1L, 2L, 3L);
        var survivor = SurvivorPolicy.Evaluate(group, eligible, selected);
        var recovery = SelectionAccounting.Calculate(group, eligible, selected);

        Assert.Equal(SurvivorDecisionKind.Allowed, survivor.Kind);
        Assert.Equal(SurvivorUnavailableReason.None, survivor.Reason);
        Assert.Equal(1, survivor.RemainingKnownPathOccurrences);
        Assert.Equal(2, survivor.RemainingPhysicalKeys);
        var external = Assert.Single(survivor.ExternalSurvivors);
        Assert.Equal(externalKey, external.PhysicalFileKey);
        Assert.Equal((uint)3, external.LinkCount);
        Assert.Equal(2, external.KnownPathCount);
        Assert.Equal(new SelectionRecovery(2048, SelectionRecoveryUnavailableReason.None), recovery);
    }
}
