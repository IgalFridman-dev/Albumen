using PhotoSift.Analysis;
using Xunit;

namespace PhotoSift.Analysis.Tests.Boundary;

public sealed class AnalysisSeedTests
{
    [Fact]
    public void ProductionAssemblyExportsOnlyItsStructuralMarker()
    {
        var exportedTypes = typeof(AssemblyMarker).Assembly.GetExportedTypes();

        Assert.Equal([typeof(AssemblyMarker)], exportedTypes);
    }
}
