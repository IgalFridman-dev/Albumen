using System.Collections.Immutable;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Scan;

public sealed class ExactIdentityTests
{
    private const string Sha256 = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

    [Fact]
    public void FileIdentityRetainsBothNativeHalves()
    {
        var key = new PhysicalFileKey(7, new FileId128(ulong.MaxValue, 11));

        Assert.Equal((ulong)7, key.VolumeSerial);
        Assert.Equal(ulong.MaxValue, key.FileId.High);
        Assert.Equal((ulong)11, key.FileId.Low);
    }

    [Fact]
    public void ContentIdentityIncludesThePhysicalFile()
    {
        var first = new PhysicalFileKey(7, new FileId128(0, 11));
        var second = new PhysicalFileKey(7, new FileId128(0, 12));

        Assert.NotEqual(new ContentRevision(first, 3, Sha256), new ContentRevision(second, 3, Sha256));
        Assert.Equal(new ContentRevision(first, 3, Sha256), new ContentRevision(first, 3, Sha256.ToLowerInvariant()));
    }

    [Fact]
    public void ContentRevisionNormalizesValidHexadecimalDigest()
    {
        var revision = new ContentRevision(
            new PhysicalFileKey(7, new FileId128(0, 11)),
            3,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        Assert.Equal(Sha256, revision.Sha256);
    }

    [Fact]
    public void ContentRevisionRejectsNegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentRevision(default, -1, Sha256));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEF")]
    [InlineData("ZA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    public void ContentRevisionRejectsMalformedDigest(string digest)
    {
        Assert.Throws<ArgumentException>(
            () => new ContentRevision(default, 0, digest));
    }

    [Fact]
    public void ContentRevisionRejectsNullDigest()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ContentRevision(default, 0, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CandidateGroupRejectsNonPositiveRevision(long revision)
    {
        var pin = new ScanPin(Guid.NewGuid(), 1, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CandidateGroup(
            Guid.NewGuid(), revision, pin, CandidateCategory.Exact,
            ImmutableArray<ScanFileOccurrence>.Empty,
            ImmutableArray<EqualityEvidence>.Empty,
            false, null, new PotentialRecovery(0, RecoveryUnavailableReason.None)));
    }

    [Fact]
    public void CandidateGroupCannotBeCopiedWithNonPositiveRevision()
    {
        var pin = new ScanPin(Guid.NewGuid(), 1, 1, 1);
        var group = new CandidateGroup(
            Guid.NewGuid(), 1, pin, CandidateCategory.Exact,
            ImmutableArray<ScanFileOccurrence>.Empty,
            ImmutableArray<EqualityEvidence>.Empty,
            false, null, new PotentialRecovery(0, RecoveryUnavailableReason.None));

        Assert.Throws<ArgumentOutOfRangeException>(() => group with { GroupRevision = 0 });
    }
}
