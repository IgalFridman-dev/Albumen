using System.Reflection;
using System.Text.RegularExpressions;
using PhotoSift.Application.Review;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Architecture;

public sealed class ActionBoundaryTests
{
    private static readonly string[] ForbiddenTypeNames =
    [
        "ActionCoordinator",
        "Requested",
        "Executing",
        "Recycled",
        "NotRemoved",
        "UnknownPendingRevalidation",
        "IFileOperation",
    ];

    [Fact]
    public void ApprovalSurfaceExistsOnlyAsAnImmutableApplicationHandoff()
    {
        var application = typeof(ApprovedActionRequest).Assembly;
        var coordinatorMethod = typeof(IReviewCoordinator).GetMethod("ApproveAsync");

        Assert.NotNull(coordinatorMethod);
        Assert.Equal(typeof(Task<ReviewCommandResult>), coordinatorMethod.ReturnType);
        Assert.Equal("PhotoSift.Application", application.GetName().Name);
        Assert.All(ForbiddenTypeNames, forbidden =>
            Assert.DoesNotContain(application.GetTypes(), type =>
                StringComparer.Ordinal.Equals(type.Name, forbidden)));
    }

    [Fact]
    public void ReviewApprovalSourceContainsNoActionStateJournalOrOsMutationBoundary()
    {
        var root = FindRepositoryRoot();
        var reviewRoot = Path.Combine(root, "src", "PhotoSift.Application", "Review");
        var reviewSource = string.Join('\n', Directory.EnumerateFiles(reviewRoot, "*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));
        var sqliteSource = File.ReadAllText(Path.Combine(root, "src",
            "PhotoSift.Infrastructure.Sqlite", "Review", "SqliteOverrideEvidenceStore.cs"));

        Assert.All(ForbiddenTypeNames, forbidden => Assert.DoesNotMatch(
            $@"\b(class|record|interface|enum)\s+{Regex.Escape(forbidden)}\b", reviewSource));
        Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", reviewSource);
        Assert.DoesNotContain("action_journal", sqliteSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actual recovered", reviewSource, StringComparison.OrdinalIgnoreCase);
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
