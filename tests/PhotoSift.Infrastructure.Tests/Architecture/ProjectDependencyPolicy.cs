using System.Xml.Linq;

namespace PhotoSift.Infrastructure.Tests.Architecture;

internal static class ProjectDependencyPolicy
{
    private static readonly Dictionary<string, IReadOnlySet<string>> AllowedReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["PhotoSift.Domain"] = CreateSet(),
            ["PhotoSift.Application"] = CreateSet("PhotoSift.Domain"),
            ["PhotoSift.Analysis"] = CreateSet("PhotoSift.Application", "PhotoSift.Domain"),
            ["PhotoSift.Infrastructure.Sqlite"] = CreateSet("PhotoSift.Application", "PhotoSift.Domain"),
            ["PhotoSift.Infrastructure.Windows"] = CreateSet("PhotoSift.Application", "PhotoSift.Domain"),
            ["PhotoSift.App"] = CreateSet(
                "PhotoSift.Domain",
                "PhotoSift.Application",
                "PhotoSift.Analysis",
                "PhotoSift.Infrastructure.Sqlite",
                "PhotoSift.Infrastructure.Windows"),
        };

    public static IReadOnlyList<string> FindViolations(
        IReadOnlyDictionary<string, IReadOnlySet<string>> projectReferences)
    {
        var violations = new List<string>();

        foreach (var (project, references) in projectReferences)
        {
            if (!AllowedReferences.TryGetValue(project, out var allowed))
            {
                violations.Add($"{project} is not a recognized production project.");
                continue;
            }

            foreach (var reference in references)
            {
                if (!allowed.Contains(reference))
                {
                    violations.Add($"{project} -> {reference} is forbidden.");
                }
            }
        }

        violations.Sort(StringComparer.Ordinal);
        return violations;
    }

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> LoadProductionGraph(
        string repositoryRoot)
    {
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var projectPath in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.csproj",
                     SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var document = XDocument.Load(projectPath, LoadOptions.None);
            var references = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFileNameWithoutExtension(include!))
                .ToHashSet(StringComparer.Ordinal);

            graph.Add(projectName, references);
        }

        return graph;
    }

    private static HashSet<string> CreateSet(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
