using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Scope;

public sealed class WindowsTraversalIntegrationTests
{
    [Fact]
    public async Task RealScanEnumerationEmitsUnicodeAndLongPathsWithPhysicalIdentity()
    {
        var root = Directory.CreateTempSubdirectory("PhotoSiftScanTraversal-");
        try
        {
            var unicodePath = Path.Combine(root.FullName, "רחוב-日本語.PNG");
            File.WriteAllText(unicodePath, "fixture-one");
            var deep = root.FullName;
            for (var index = 0; index < 4; index++)
            {
                deep = Path.Combine(deep, $"segment-{index}-{new string('x', 58)}");
            }

            Assert.True(deep.Length > 260, $"Fixture path length was only {deep.Length}.");
            Directory.CreateDirectory(deep);
            var deepPath = Path.Combine(deep, "deep.jpeg");
            File.WriteAllText(deepPath, "fixture-two");
            var sourceTruth = new Dictionary<string, SourceTruth>(StringComparer.OrdinalIgnoreCase)
            {
                [unicodePath] = ReadSourceTruth(unicodePath),
                [deepPath] = ReadSourceTruth(deepPath),
            };
            var drive = new DriveInfo(Path.GetPathRoot(root.FullName)!);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"E5-S2 environment: os={Environment.OSVersion.VersionString}; " +
                $"processors={Environment.ProcessorCount}; " +
                $"availableMemory={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}; " +
                $"driveType={drive.DriveType}; format={drive.DriveFormat}; " +
                $"root={drive.RootDirectory.FullName}");

            var selection = new ScopeSelection(
                root.FullName,
                ScopeRecursion.IncludeSubfolders);
            var evidence = await new WindowsLocalScopePreflight().InspectAsync(
                selection,
                CancellationToken.None);
            var scope = Assert.IsType<ResolvedScope>(evidence.Scope);
            var context = new ScanRunContext(
                new ScanPin(
                    new Guid("33333333-3333-3333-3333-333333333333"),
                    1,
                    1,
                    1),
                scope);
            var events = new List<ScanEnumerationEvent>();

            await foreach (var item in new WindowsScanEnumerator().EnumerateAsync(
                context,
                TestContext.Current.CancellationToken))
            {
                events.Add(item);
            }

            var files = events.OfType<ScanFileFound>().Select(item => item.File).ToArray();
            Assert.Equal(2, files.Length);
            Assert.All(files, file =>
            {
                Assert.NotNull(file.PhysicalFileKey);
                Assert.Equal(
                    new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
                    file.Outcome);
            });
            Assert.Contains(files, file => Path.GetFileName(file.DiscoveredPath) == "רחוב-日本語.PNG");
            Assert.Contains(files, file => file.ResolvedPath == deepPath);
            foreach (var source in sourceTruth)
            {
                var file = Assert.Single(files, candidate => candidate.ResolvedPath == source.Key);
                Assert.Equal(source.Value.PhysicalFileKey, file.PhysicalFileKey);
                Assert.Equal(source.Value, ReadSourceTruth(source.Key));
            }
            Assert.Equal(
                new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
                Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
        }
        finally
        {
            CleanupAll(() => root.Delete(recursive: true));
        }
    }

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
            CleanupAll(() => root.Delete(recursive: true));
        }
    }

    [Fact]
    public async Task RealReadLockedJpegIsCountedUnreadable()
    {
        var root = Directory.CreateTempSubdirectory("PhotoSiftLocked-");
        try
        {
            var path = Path.Combine(root.FullName, "locked.jpg");
            File.WriteAllText(path, "fixture");
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
            CleanupAll(() => root.Delete(recursive: true));
        }
    }

    [Fact]
    public async Task RealAccessDeniedDescendantBlocksCompleteCountsAndRestoresAcl()
    {
        DirectoryInfo? denied = null;
        DirectorySecurity? restricted = null;
        FileSystemAccessRule? denial = null;
        var restrictionApplied = false;
        var root = Directory.CreateTempSubdirectory("PhotoSiftDenied-");

        try
        {
            denied = Directory.CreateDirectory(Path.Combine(root.FullName, "denied"));
            File.WriteAllText(Path.Combine(denied.FullName, "hidden.jpg"), "fixture");
            var security = denied.GetAccessControl(AccessControlSections.Access);
            var user = WindowsIdentity.GetCurrent().User ??
                throw new InvalidOperationException("The fixture requires a Windows user SID.");
            denial = new FileSystemAccessRule(
                user,
                FileSystemRights.ListDirectory,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny);
            restricted = new DirectorySecurity();
            restricted.SetSecurityDescriptorBinaryForm(
                security.GetSecurityDescriptorBinaryForm());
            restricted.AddAccessRule(denial);
            denied.SetAccessControl(restricted);
            restrictionApplied = true;

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
            CleanupAll(
                () =>
                {
                    if (restrictionApplied)
                    {
                        restricted!.RemoveAccessRuleSpecific(denial!);
                        denied!.SetAccessControl(restricted);
                    }
                },
                () => root.Delete(recursive: true));
        }
    }

    [Fact]
    public async Task RealContainedEscapingAndCyclicLinksStayWithinResolvedScope()
    {
        DirectoryInfo? outside = null;
        string? alias = null;
        string? escape = null;
        string? cycle = null;
        var aliasCreated = false;
        var escapeCreated = false;
        var cycleCreated = false;
        var root = Directory.CreateTempSubdirectory("PhotoSiftLinks-");

        try
        {
            outside = Directory.CreateTempSubdirectory("PhotoSiftOutside-");
            var actual = Directory.CreateDirectory(Path.Combine(root.FullName, "actual"));
            File.WriteAllText(Path.Combine(actual.FullName, "inside.jpg"), "fixture");
            File.WriteAllText(Path.Combine(outside.FullName, "outside.jpg"), "fixture");
            alias = Path.Combine(root.FullName, "alias");
            escape = Path.Combine(root.FullName, "escape");
            cycle = Path.Combine(actual.FullName, "cycle");

            Directory.CreateSymbolicLink(alias, actual.FullName);
            aliasCreated = true;
            Directory.CreateSymbolicLink(escape, outside.FullName);
            escapeCreated = true;
            Directory.CreateSymbolicLink(cycle, root.FullName);
            cycleCreated = true;

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
            CleanupAll(
                () =>
                {
                    if (cycleCreated)
                    {
                        DeleteDirectoryLink(cycle!);
                    }
                },
                () =>
                {
                    if (aliasCreated)
                    {
                        DeleteDirectoryLink(alias!);
                    }
                },
                () =>
                {
                    if (escapeCreated)
                    {
                        DeleteDirectoryLink(escape!);
                    }
                },
                () => root.Delete(recursive: true),
                () =>
                {
                    if (outside is not null)
                    {
                        outside.Delete(recursive: true);
                    }
                });
        }
    }

    private static SourceTruth ReadSourceTruth(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var facts = new WindowsFileNative().Read(stream.SafeFileHandle, path);
        var key = facts.PhysicalFileKey
            ?? throw new InvalidDataException($"Source identity was unavailable: {path}");
        return new SourceTruth(key, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void DeleteDirectoryLink(string path) => Directory.Delete(path);

    private static void CleanupAll(params Action[] cleanupActions)
    {
        var failures = new List<Exception>();
        foreach (var cleanupAction in cleanupActions)
        {
            var failure = Record.Exception(cleanupAction);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Fixture cleanup failed.", failures);
        }
    }

    private sealed record SourceTruth(PhysicalFileKey PhysicalFileKey, string Sha256);
}
