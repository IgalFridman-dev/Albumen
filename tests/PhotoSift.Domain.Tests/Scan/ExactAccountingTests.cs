using System.Collections.Immutable;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Scan;

public sealed class ExactAccountingTests
{
    private const string ShaA = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
    private const string ShaB = "CB8379AC2098AA165029E3938A51DA0BCECFC008FD6795F401178647F96C5B34";

    [Fact]
    public void AliasOnlyEvidenceHasZeroPotentialRecovery()
    {
        var key = Key(1);
        var members = ImmutableArray.Create(
            Occurrence(1, key, @"C:\Photos\one.jpg", 80, 2, ShaA),
            Occurrence(2, key, @"C:\Photos\alias.jpg", 80, 2, ShaA));

        Assert.Equal(
            new PotentialRecovery(0, RecoveryUnavailableReason.None),
            ExactAccounting.Calculate(members));
    }

    [Fact]
    public void DistinctPhysicalCopiesCountAllocationOncePerIdentity()
    {
        var first = Key(1);
        var second = Key(2);
        var members = ImmutableArray.Create(
            Occurrence(1, first, @"C:\Photos\one.jpg", 100, 2, ShaA),
            Occurrence(2, first, @"C:\Photos\alias.jpg", 100, 2, ShaA),
            Occurrence(3, second, @"C:\Photos\copy.jpg", 70, 1, ShaA));

        Assert.Equal(
            new PotentialRecovery(100, RecoveryUnavailableReason.None),
            ExactAccounting.Calculate(members));
    }

    [Fact]
    public void RepeatedResolvedPathUsesWindowsCaseInsensitivePathEqualityForLinkCoverage()
    {
        var key = Key(1);
        var members = ImmutableArray.Create(
            Occurrence(1, key, @"C:\Photos\one.jpg", 80, 1, ShaA),
            Occurrence(2, key, @"c:\photos\ONE.JPG", 80, 1, ShaA));

        Assert.Equal(
            new PotentialRecovery(0, RecoveryUnavailableReason.None),
            ExactAccounting.Calculate(members));
    }

    [Fact]
    public void UnobservedLinksMakeRecoveryUnavailable()
    {
        var result = ExactAccounting.Calculate(
            ImmutableArray.Create(Occurrence(1, Key(1), @"C:\Photos\one.jpg", 80, 2, ShaA)));

        Assert.Equal(new PotentialRecovery(null, RecoveryUnavailableReason.ExternalLinks), result);
    }

    [Fact]
    public void MissingAllocationMakesRecoveryUnavailable()
    {
        var result = ExactAccounting.Calculate(
            ImmutableArray.Create(Occurrence(1, Key(1), @"C:\Photos\one.jpg", null, 1, ShaA)));

        Assert.Equal(new PotentialRecovery(null, RecoveryUnavailableReason.UnknownAllocation), result);
    }

    [Fact]
    public void MissingLinkCountMakesRecoveryUnavailable()
    {
        var result = ExactAccounting.Calculate(
            ImmutableArray.Create(Occurrence(1, Key(1), @"C:\Photos\one.jpg", 80, null, ShaA)));

        Assert.Equal(new PotentialRecovery(null, RecoveryUnavailableReason.UnknownLinks), result);
    }

    [Fact]
    public void UnstableAllocationEvidenceMakesRecoveryUnavailable()
    {
        var result = ExactAccounting.Calculate(
            ImmutableArray.Create(Occurrence(1, Key(1), @"C:\Photos\one.jpg", 80, 1, ShaA, stable: false)));

        Assert.Equal(new PotentialRecovery(null, RecoveryUnavailableReason.InconsistentFacts), result);
    }

    [Fact]
    public void DisagreeingFactsForOnePhysicalIdentityMakeRecoveryUnavailable()
    {
        var key = Key(1);
        var members = ImmutableArray.Create(
            Occurrence(1, key, @"C:\Photos\one.jpg", 80, 2, ShaA),
            Occurrence(2, key, @"C:\Photos\alias.jpg", 96, 3, ShaA));

        Assert.Equal(
            new PotentialRecovery(null, RecoveryUnavailableReason.InconsistentFacts),
            ExactAccounting.Calculate(members));
    }

    [Fact]
    public void DisagreeingContentForOnePhysicalIdentityMakesRecoveryUnavailable()
    {
        var key = Key(1);
        var members = ImmutableArray.Create(
            Occurrence(1, key, @"C:\Photos\one.jpg", 80, 2, ShaA),
            Occurrence(2, key, @"C:\Photos\alias.jpg", 80, 2, ShaB));

        Assert.Equal(
            new PotentialRecovery(null, RecoveryUnavailableReason.InconsistentFacts),
            ExactAccounting.Calculate(members));
    }

    [Fact]
    public void InvalidOrMissingPhysicalFactsMakeRecoveryUnavailable()
    {
        var missingIdentity = Occurrence(1, null, @"C:\Photos\one.jpg", 80, 1, contentSha: null);
        var negativeAllocation = Occurrence(2, Key(2), @"C:\Photos\two.jpg", -1, 1, ShaA);

        Assert.Equal(
            new PotentialRecovery(null, RecoveryUnavailableReason.InconsistentFacts),
            ExactAccounting.Calculate(ImmutableArray.Create(missingIdentity)));
        Assert.Equal(
            new PotentialRecovery(null, RecoveryUnavailableReason.InconsistentFacts),
            ExactAccounting.Calculate(ImmutableArray.Create(negativeAllocation)));
    }

    [Fact]
    public void CheckedAllocationArithmeticReportsOverflow()
    {
        var members = ImmutableArray.Create(
            Occurrence(1, Key(1), @"C:\Photos\one.jpg", long.MaxValue, 1, ShaA),
            Occurrence(2, Key(2), @"C:\Photos\two.jpg", long.MaxValue, 1, ShaA));

        Assert.Equal(
            new PotentialRecovery(null, RecoveryUnavailableReason.Overflow),
            ExactAccounting.Calculate(members));
    }

    [Fact]
    public void EmptyEvidenceHasZeroPotentialRecovery()
    {
        Assert.Equal(
            new PotentialRecovery(0, RecoveryUnavailableReason.None),
            ExactAccounting.Calculate(ImmutableArray<ScanFileOccurrence>.Empty));
    }

    private static PhysicalFileKey Key(ulong low) => new(7, new FileId128(0, low));

    private static ScanFileOccurrence Occurrence(
        long id,
        PhysicalFileKey? key,
        string resolvedPath,
        long? allocatedBytes,
        uint? linkCount,
        string? contentSha,
        bool stable = true)
    {
        var pin = new ScanPin(new Guid("11111111-1111-1111-1111-111111111111"), 2, 3, 4);
        var token = new OccurrenceToken(pin, id, 1, 1);
        var succeeded = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var content = key is not null && contentSha is not null
            ? new ContentRevision(key.Value, 3, contentSha)
            : null;

        return new ScanFileOccurrence(
            token,
            resolvedPath,
            resolvedPath,
            key,
            succeeded,
            content is null
                ? new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety)
                : succeeded,
            content,
            new AllocationEvidence(allocatedBytes, linkCount, stable));
    }
}
