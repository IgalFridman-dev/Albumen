using System.Reflection;
using System.Text.RegularExpressions;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Action;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Architecture;

public sealed class ActionBoundaryTests
{
    private static readonly string[] ActionOwnerTypeNames =
    [
        "ActionCoordinator", "ActionStore", "ActionOwnerInbox", "IActionCoordinator",
        "ActionSnapshot", "ActionItemSnapshot", "ActionCommandResult", "IActionJournalStore", "IRecycleBinExecutor",
    ];

    [Fact]
    public void ActionOwnerAndBusinessPortsBelongOnlyToApplicationWhilePolicyRemainsPureDomain()
    {
        var application = typeof(ApprovedActionRequest).Assembly;
        var coordinatorMethod = typeof(IReviewCoordinator).GetMethod("ApproveAsync");

        Assert.NotNull(coordinatorMethod);
        Assert.Equal(typeof(Task<ReviewCommandResult>), coordinatorMethod.ReturnType);
        Assert.Equal("PhotoSift.Application", application.GetName().Name);
        Assert.All(ActionOwnerTypeNames, name =>
        {
            var type = Assert.Single(application.GetTypes(), type => StringComparer.Ordinal.Equals(type.Name, name));
            Assert.Equal("PhotoSift.Application.Action", type.Namespace);
            Assert.DoesNotContain(typeof(ActionSafetyPolicy).Assembly.GetTypes(), value => value.Name == name);
        });
        Assert.True(application.GetType("PhotoSift.Application.Action.ActionStore")!.IsNotPublic);
        Assert.True(typeof(IActionCoordinator).IsAssignableFrom(typeof(ActionCoordinator)));
        Assert.Equal(typeof(Task<ActionCommandResult>), typeof(IActionCoordinator).GetMethod("ApproveAsync")!.ReturnType);
        Assert.Equal(typeof(ApprovedActionRequest), typeof(IActionCoordinator).GetMethod("ApproveAsync")!.GetParameters()[0].ParameterType);
        Assert.Equal("PhotoSift.Domain", typeof(ActionSafetyPolicy).Assembly.GetName().Name);
        Assert.DoesNotContain(application.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || reference.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || reference.Name == "PresentationFramework");
    }

    [Fact]
    public void ReviewApprovalSourceContainsNoActionStateJournalOrOsMutationBoundary()
    {
        var root = FindRepositoryRoot();
        var reviewRoot = Path.Combine(root, "src", "PhotoSift.Application", "Review");
        var reviewSource = string.Join('\n', Directory.EnumerateFiles(reviewRoot, "*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));
        Assert.All(ActionOwnerTypeNames, forbidden => Assert.DoesNotMatch(
            $@"\b(class|record|interface|enum)\s+{Regex.Escape(forbidden)}\b", reviewSource));
        Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", reviewSource);
        Assert.DoesNotMatch(@"\b(ActionStore|ActionItemSnapshot|ActionSnapshot|IActionJournalStore|IRecycleBinExecutor)\b", reviewSource);
        Assert.DoesNotContain("actual recovered", reviewSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DomainAndApplicationCannotOwnProviderComOrSourceMutationApis()
    {
        var root = FindRepositoryRoot();
        foreach (var project in new[] { "PhotoSift.Domain", "PhotoSift.Application" })
        {
            var source = string.Join('\n', Sources(Path.Combine(root, "src", project)).Select(File.ReadAllText));
            Assert.DoesNotMatch(@"\b(IFileOperation|IShellItem|Microsoft\.Data\.Sqlite|SqliteConnection|SqliteTransaction|DllImport|LibraryImport|ComImport|GeneratedComInterface|GeneratedComClass)\b", source);
            Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", source);
            Assert.DoesNotMatch(@"\b(DeleteFile|RemoveDirectory|MoveFile|SHFileOperation|FOF_ALLOWUNDO|FOFX_EARLYFAILURE|FOFX_NORECURSION)\b", source);
        }
    }

    [Fact]
    public void NativeAndJournalImplementationsStayInTheirInfrastructureProjectsAndCannotFallback()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src");
        foreach (var file in Sources(root))
        {
            var relative = Path.GetRelativePath(root, file);
            var source = File.ReadAllText(file);
            if (!relative.StartsWith("PhotoSift.Infrastructure.Windows" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                Assert.DoesNotMatch(@"\b(IFileOperation|IShellItem|ComImport|GeneratedComInterface|GeneratedComClass)\b", source);
            if (!relative.StartsWith("PhotoSift.Infrastructure.Sqlite" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(@"\b(SqliteActionJournalStore|Microsoft\.Data\.Sqlite|SqliteConnection)\b", source);
                Assert.DoesNotMatch(@"\bclass\s+\w+[^\r\n{]*:\s*IActionJournalStore\b", source);
            }
            if (relative.Contains(Path.DirectorySeparatorChar + "Action" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", source);
                Assert.DoesNotMatch(@"\b(DeleteFile|DeleteFileW|RemoveDirectory|RemoveDirectoryW|SHFileOperation|SHFileOperationW|FOF_WANTNUKEWARNING)\b", source);
                Assert.DoesNotContain("permanent-delete fallback", source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ApprovalAndActionSnapshotsExposeOnlyImmutableValuesAndSelectionIsNeverReconstructed()
    {
        foreach (var type in new[] { typeof(ApprovedActionRequest), typeof(ApprovedActionItem), typeof(ActionSnapshot), typeof(ActionItemSnapshot) })
        {
            Assert.True(type.IsSealed);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.SetMethod is { } setter)
                    Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        }
        var root = Path.Combine(FindRepositoryRoot(), "src");
        foreach (var file in Sources(root).Where(file => !file.EndsWith(Path.Combine("Review", "ReviewStore.cs"), StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"\bnew\s+ApprovedActionRequest\s*\(", File.ReadAllText(file));
    }

    private static IEnumerable<string> Sources(string root) => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

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
