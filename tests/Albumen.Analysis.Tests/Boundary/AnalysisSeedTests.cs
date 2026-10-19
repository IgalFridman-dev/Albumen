using Albumen.Analysis;
using Albumen.Analysis.Exact;
using Albumen.Analysis.NearDuplicate;
using Albumen.Application.Scan;
using Xunit;

namespace Albumen.Analysis.Tests.Boundary;

public sealed class AnalysisSeedTests
{
    [Fact]
    public void ProductionAssemblyExportsOnlyItsMarkerAndImplementedAnalyzers()
    {
        var exportedTypes = typeof(AssemblyMarker).Assembly.GetExportedTypes().OrderBy(type => type.FullName).ToArray();

        Assert.Equal(
            [typeof(AssemblyMarker), typeof(StreamingExactContentAnalyzer), typeof(ManagedPerceptualSignature)],
            exportedTypes);
        Assert.Contains(typeof(IExactContentAnalyzer), typeof(StreamingExactContentAnalyzer).GetInterfaces());
    }

    [Fact]
    public void ExactAnalyzerPublicOperationsAcceptStreamsWithoutPaths()
    {
        var methods = typeof(StreamingExactContentAnalyzer)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(StreamingExactContentAnalyzer))
            .OrderBy(method => method.Name)
            .ToArray();

        Assert.Equal(["CompareAsync", "HashAsync"], methods.Select(method => method.Name));
        Assert.All(
            methods,
            method =>
            {
                Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(Stream));
                Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(string));
            });
    }
}
