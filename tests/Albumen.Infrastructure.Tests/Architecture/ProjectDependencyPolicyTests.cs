using Xunit;

namespace Albumen.Infrastructure.Tests.Architecture;

public sealed class ProjectDependencyPolicyTests
{
    private static readonly string[] ExpectedProjects =
    [
        "Albumen.Analysis",
        "Albumen.App",
        "Albumen.Application",
        "Albumen.Domain",
        "Albumen.Infrastructure.Onnx",
        "Albumen.Infrastructure.Sqlite",
        "Albumen.Infrastructure.Windows",
    ];

    [Fact]
    public void ForbiddenApplicationToAdapterReferenceIsRejected()
    {
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Albumen.Application"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "Albumen.Domain",
                "Albumen.Infrastructure.Windows",
            },
        };

        var violation = Assert.Single(ProjectDependencyPolicy.FindViolations(graph));

        Assert.Equal(
            "Albumen.Application -> Albumen.Infrastructure.Windows is forbidden.",
            violation);
    }

    [Fact]
    public void ActualProductionProjectReferencesFollowPolicy()
    {
        var graph = ProjectDependencyPolicy.LoadProductionGraph(FindRepositoryRoot());

        Assert.Equal(ExpectedProjects, graph.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(ProjectDependencyPolicy.FindViolations(graph));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Albumen.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Albumen repository root.");
    }
}
