using Xunit;

namespace PhotoSift.Infrastructure.Tests.Architecture;

public sealed class ProjectDependencyPolicyTests
{
    private static readonly string[] ExpectedProjects =
    [
        "PhotoSift.Analysis",
        "PhotoSift.App",
        "PhotoSift.Application",
        "PhotoSift.Domain",
        "PhotoSift.Infrastructure.Sqlite",
        "PhotoSift.Infrastructure.Windows",
    ];

    [Fact]
    public void ForbiddenApplicationToAdapterReferenceIsRejected()
    {
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["PhotoSift.Application"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "PhotoSift.Domain",
                "PhotoSift.Infrastructure.Windows",
            },
        };

        var violation = Assert.Single(ProjectDependencyPolicy.FindViolations(graph));

        Assert.Equal(
            "PhotoSift.Application -> PhotoSift.Infrastructure.Windows is forbidden.",
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
            if (File.Exists(Path.Combine(directory.FullName, "PhotoSift.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the PhotoSift repository root.");
    }
}
