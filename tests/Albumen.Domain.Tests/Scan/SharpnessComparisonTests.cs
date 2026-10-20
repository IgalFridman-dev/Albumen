using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Scan;

public sealed class SharpnessComparisonTests
{
    [Fact]
    public void SharpestNeedsAClearMarginOverTheRunnerUp()
    {
        Assert.Equal(2, SharpnessComparison.Sharpest([(1, 400), (2, 500), (3, 100)]));
        Assert.Null(SharpnessComparison.Sharpest([(1, 450), (2, 500), (3, 100)]));
    }

    [Fact]
    public void NoSharpestWhenAnyPhotoIsUnmeasuredOrTheSetHasTooLittleDetail()
    {
        Assert.Null(SharpnessComparison.Sharpest([(1, 100), (2, 900), (3, null)]));
        Assert.Null(SharpnessComparison.Sharpest([(1, 2), (2, 20)]));
        Assert.Null(SharpnessComparison.Sharpest([(1, 900)]));
    }

    [Fact]
    public void BlurrierMeansAQuarterOrLessOfTheSharpestDetail()
    {
        var blurrier = SharpnessComparison.Blurrier([(1, 800), (2, 200), (3, 201), (4, 50), (5, null)]);

        Assert.Equal([2L, 4L], blurrier.Order());
    }

    [Fact]
    public void NothingLooksBlurrierInAFlatSetOrWithoutAComparison()
    {
        Assert.Empty(SharpnessComparison.Blurrier([(1, 20), (2, 1)]));
        Assert.Empty(SharpnessComparison.Blurrier([(1, 900), (2, null)]));
        Assert.Empty(SharpnessComparison.Blurrier([]));
    }
}
