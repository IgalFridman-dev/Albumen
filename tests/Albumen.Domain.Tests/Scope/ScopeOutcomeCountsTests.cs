using System.Collections.Immutable;
using Albumen.Domain.Scope;
using Xunit;

namespace Albumen.Domain.Tests.Scope;

public sealed class ScopeOutcomeCountsTests
{
    [Fact]
    public void ScopeOutcomeCountsHasNoSampleNamesUnlessGivenSome()
    {
        var counts = new ScopeOutcomeCounts(3, 0, 0, 0);

        Assert.False(counts.SampleSupportedFiles.IsDefault);
        Assert.Empty(counts.SampleSupportedFiles);
    }

    [Fact]
    public void ScopeOutcomeCountsKeepsUpToSixSampleNamesInOrder()
    {
        ImmutableArray<string> names = ["a.jpg", "b.jpg", "c.png", "d.jpg", "e.jpg", "f.jpg"];

        var counts = new ScopeOutcomeCounts(18, 0, 0, 0, names);

        Assert.Equal(names, counts.SampleSupportedFiles);
    }

    [Fact]
    public void ScopeOutcomeCountsRejectsMoreThanSixSampleNames()
    {
        ImmutableArray<string> names = ["a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg", "f.jpg", "g.jpg"];

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScopeOutcomeCounts(7, 0, 0, 0, names));

        Assert.Equal("sampleSupportedFiles", exception.ParamName);
    }

    [Fact]
    public void ScopeOutcomeCountsCompareSampleNamesByValue()
    {
        var first = new ScopeOutcomeCounts(2, 0, 0, 0, ["a.jpg", "b.jpg"]);
        var same = new ScopeOutcomeCounts(2, 0, 0, 0, ["a.jpg", "b.jpg"]);
        var other = new ScopeOutcomeCounts(2, 0, 0, 0, ["a.jpg", "c.jpg"]);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, new ScopeOutcomeCounts(2, 0, 0, 0));
    }
}
