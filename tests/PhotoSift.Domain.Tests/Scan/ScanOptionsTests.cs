using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Domain.Tests.Scan;

public sealed class ScanOptionsTests
{
    [Fact]
    public void DefaultPinsExactAndConservativeNearWithoutSelectingAnyKeeper()
    {
        var options = ScanOptions.Default;

        Assert.Equal(ExactDuplicateMode.Enabled, options.Exact);
        Assert.Equal(NearDuplicateMode.Conservative, options.Near);
        Assert.True(options.IsValid);
    }

    [Fact]
    public void ExactCannotBeDisabledAndUnknownNearModeIsInvalid()
    {
        Assert.False(new ScanOptions(ExactDuplicateMode.Disabled, NearDuplicateMode.Conservative).IsValid);
        Assert.False(new ScanOptions(ExactDuplicateMode.Enabled, (NearDuplicateMode)99).IsValid);
    }

    [Fact]
    public void NearCanBeDisabledButInvalidValuesHavePlainGuidance()
    {
        var exactOnly = new ScanOptions(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled);
        var invalid = new ScanOptions(ExactDuplicateMode.Disabled, (NearDuplicateMode)99);

        Assert.True(exactOnly.IsValid);
        Assert.Empty(exactOnly.ValidationGuidance);
        Assert.Equal(
            "Exact must remain enabled. Near must be Disabled or Conservative.",
            invalid.ValidationGuidance);
    }
}
