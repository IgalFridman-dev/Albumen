using System.Security.AccessControl;
using System.Security.Principal;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Scope;

public sealed class WindowsTraversalIntegrationTests
{
    [Fact]
    public async Task RealFilesystemHonorsTopLevelRecursionUnicodeAndLongPaths()
    {
        var root = Directory.CreateTempSubdirectory("PhotoSiftTraversal-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "top.jpg"), "fixture");
            var unicode = Directory.CreateDirectory(Path.Combine(root.FullName, "תמונות-日本語"));
            File.WriteAllText(Path.Combine(unicode.FullName, "nested.png"), "fixture");

            var deep = root.FullName;
            for (var index = 0; index < 4; index++)
            {
                deep = Path.Combine(deep, $"segment-{index}-{new string('x', 58)}");
            }

            Assert.True(deep.Length > 260, $"Fixture path length was only {deep.Length}.");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "deep.jpeg"), "fixture");

            var adapter = new WindowsLocalScopePreflight();
            var topLevel = await adapter.InspectAsync(
                new ScopeSelection(root.FullName, ScopeRecursion.TopLevelOnly),
                CancellationToken.None);
            var recursive = await adapter.InspectAsync(
                new ScopeSelection(root.FullName, ScopeRecursion.IncludeSubfolders),
                CancellationToken.None);

            Assert.Equal(ScopeTraversalState.Complete, topLevel.Traversal);
            Assert.Equal(new ScopeOutcomeCounts(1, 0, 0, 0), topLevel.Counts);
            Assert.Equal(ScopeTraversalState.Complete, recursive.Traversal);
            Assert.Equal(new ScopeOutcomeCounts(3, 0, 0, 0), recursive.Counts);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RealReadLockedJpegIsCountedUnreadable()
    {
        var root = Directory.CreateTempSubdirectory("PhotoSiftLocked-");
        var path = Path.Combine(root.FullName, "locked.jpg");
        File.WriteAllText(path, "fixture");

        try
        {
            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var evidence = await new WindowsLocalScopePreflight().InspectAsync(
                new ScopeSelection(root.FullName, ScopeRecursion.IncludeSubfolders),
                CancellationToken.None);

            Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
            Assert.Equal(new ScopeOutcomeCounts(0, 0, 1, 0), evidence.Counts);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RealAccessDeniedDescendantBlocksCompleteCountsAndRestoresAcl()
    {
        var root = Directory.CreateTempSubdirectory("PhotoSiftDenied-");
        var denied = Directory.CreateDirectory(Path.Combine(root.FullName, "denied"));
        File.WriteAllText(Path.Combine(denied.FullName, "hidden.jpg"), "fixture");
        var security = denied.GetAccessControl(AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The fixture requires a Windows user SID.");
        var denial = new FileSystemAccessRule(
            user,
            FileSystemRights.ListDirectory,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        var restricted = new DirectorySecurity();
        restricted.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm());
        restricted.AddAccessRule(denial);
        denied.SetAccessControl(restricted);

        try
        {
            var evidence = await new WindowsLocalScopePreflight().InspectAsync(
                new ScopeSelection(root.FullName, ScopeRecursion.IncludeSubfolders),
                CancellationToken.None);
            var result = ScopePreflightPolicy.Evaluate(evidence);

            Assert.Equal(ScopeTraversalState.DescendantAccessDenied, evidence.Traversal);
            Assert.Null(result.CompleteCounts);
            Assert.Contains(
                ScopeBlockingReason.DescendantAccessDenied,
                result.BlockingReasons);
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(denial);
            denied.SetAccessControl(restricted);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RealContainedEscapingAndCyclicLinksStayWithinResolvedScope()
    {
        var root = Directory.CreateTempSubdirectory("PhotoSiftLinks-");
        var outside = Directory.CreateTempSubdirectory("PhotoSiftOutside-");
        var actual = Directory.CreateDirectory(Path.Combine(root.FullName, "actual"));
        File.WriteAllText(Path.Combine(actual.FullName, "inside.jpg"), "fixture");
        File.WriteAllText(Path.Combine(outside.FullName, "outside.jpg"), "fixture");
        var alias = Path.Combine(root.FullName, "alias");
        var escape = Path.Combine(root.FullName, "escape");
        var cycle = Path.Combine(actual.FullName, "cycle");

        try
        {
            Directory.CreateSymbolicLink(alias, actual.FullName);
            Directory.CreateSymbolicLink(escape, outside.FullName);
            Directory.CreateSymbolicLink(cycle, root.FullName);

            var evidence = await new WindowsLocalScopePreflight().InspectAsync(
                new ScopeSelection(root.FullName, ScopeRecursion.IncludeSubfolders),
                CancellationToken.None);

            Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
            Assert.Equal(new ScopeOutcomeCounts(1, 0, 0, 0), evidence.Counts);
            Assert.Equal(1, evidence.Restrictions.EscapingReparseTargets);
            Assert.Equal(2, evidence.Restrictions.DuplicateDirectoryIdentities);
        }
        finally
        {
            DeleteDirectoryLink(cycle);
            DeleteDirectoryLink(alias);
            DeleteDirectoryLink(escape);
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    private static void DeleteDirectoryLink(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
    }
}
