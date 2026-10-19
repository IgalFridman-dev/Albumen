using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scope;
using Xunit;

namespace Albumen.Infrastructure.Tests.Scope;

public sealed class WindowsLocalScopePreflightTests
{
    private static readonly string[] SelectedPathOnly = [@"C:\Photos"];
    private static readonly string[] CanonicalPathOnly = [@"D:\Canonical\Photos"];
    private static readonly string[] ResolvedRootOnly = [@"C:\Resolved\Photos"];

    [Theory]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Local, ScopeTraversalState.Complete)]
    [InlineData(ScopeVolumeKind.Removable, ScopeCloudState.Local, ScopeTraversalState.Complete)]
    [InlineData(ScopeVolumeKind.Unavailable, ScopeCloudState.Local, ScopeTraversalState.NotRun)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Synchronized, ScopeTraversalState.NotRun)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Placeholder, ScopeTraversalState.NotRun)]
    [InlineData(ScopeVolumeKind.Fixed, ScopeCloudState.Unknown, ScopeTraversalState.NotRun)]
    public async Task ReportsConservativeRootEvidence(
        ScopeVolumeKind volume,
        ScopeCloudState cloud,
        ScopeTraversalState expectedTraversal)
    {
        var native = FakeWindowsScopeNative.ForRoot(volume, cloud);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            new ScopeSelection(@"C:\Photos", ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(volume, evidence.Volume);
        Assert.Equal(cloud, evidence.Cloud);
        Assert.Equal(expectedTraversal, evidence.Traversal);
    }

    [Fact]
    public async Task UsesSelectedPathOnlyForRootInspectionAndResolvedPathForLookups()
    {
        var native = FakeWindowsScopeNative.ForRoot(
            ScopeVolumeKind.Fixed,
            ScopeCloudState.Local,
            @"C:\Photos",
            @"D:\Canonical\Photos");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            new ScopeSelection(@"C:\Photos", ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(SelectedPathOnly, native.InspectedPaths);
        Assert.Equal(CanonicalPathOnly, native.VolumePaths);
        Assert.Equal(CanonicalPathOnly, native.EnumeratedPaths);
        Assert.Equal(@"C:\Photos", evidence.Scope!.Selection.SelectedPath);
        Assert.Equal(@"D:\Canonical\Photos", evidence.Scope.ResolvedPath);
    }

    [Fact]
    public async Task TopLevelOnlyDoesNotInspectOrEnumerateDescendants()
    {
        var native = LocalRoot()
            .AddDirectory(
                @"C:\Resolved\Photos",
                @"C:\Resolved\Photos\Child",
                @"C:\Resolved\Photos\Child",
                FakeWindowsScopeNative.Identity(2))
            .AddFile(
                @"C:\Resolved\Photos\Child",
                @"C:\Resolved\Photos\Child\nested.jpg");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.TopLevelOnly),
            CancellationToken.None);

        Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
        Assert.Equal(0, evidence.Counts.Supported);
        Assert.Equal(SelectedPathOnly, native.InspectedPaths);
        Assert.Equal(ResolvedRootOnly, native.EnumeratedPaths);
    }

    [Theory]
    [InlineData(ScopeCloudState.Synchronized)]
    [InlineData(ScopeCloudState.Placeholder)]
    [InlineData(ScopeCloudState.Unknown)]
    public async Task TopLevelOnlyIgnoresOutOfScopeDescendantCloudState(
        ScopeCloudState descendantCloud)
    {
        var child = @"C:\Resolved\Photos\CloudChild";
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\direct.jpg")
            .AddDirectory(
                @"C:\Resolved\Photos",
                child,
                child,
                FakeWindowsScopeNative.Identity(2),
                entryCloud: descendantCloud,
                directoryCloud: descendantCloud);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.TopLevelOnly),
            CancellationToken.None);

        Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
        Assert.Equal(new ScopeOutcomeCounts(1, 0, 0, 0), evidence.Counts);
        Assert.Equal(SelectedPathOnly, native.InspectedPaths);
        Assert.Equal(ResolvedRootOnly, native.EnumeratedPaths);
        Assert.DoesNotContain(child, native.InspectedPaths);
        Assert.DoesNotContain(child, native.EnumeratedPaths);
    }

    [Fact]
    public async Task DescendantAccessDeniedSuppressesOtherwiseCompleteCounts()
    {
        var child = @"C:\Resolved\Photos\Denied";
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\visible.jpg")
            .AddDirectory(
                @"C:\Resolved\Photos",
                child,
                child,
                FakeWindowsScopeNative.Identity(2))
            .FailEnumeration(child, new UnauthorizedAccessException("fixture denial"));
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);
        var result = ScopePreflightPolicy.Evaluate(evidence);

        Assert.Equal(ScopeTraversalState.DescendantAccessDenied, evidence.Traversal);
        Assert.Null(result.CompleteCounts);
        Assert.Contains(ScopeBlockingReason.DescendantAccessDenied, result.BlockingReasons);
    }

    [Fact]
    public async Task RootAccessDeniedIsReportedSeparatelyFromDescendantDenial()
    {
        var native = LocalRoot()
            .FailEnumeration(
                @"C:\Resolved\Photos",
                new UnauthorizedAccessException("fixture denial"));
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(ScopeTraversalState.RootAccessDenied, evidence.Traversal);
        Assert.Contains(
            ScopeBlockingReason.RootAccessDenied,
            ScopePreflightPolicy.Evaluate(evidence).BlockingReasons);
    }

    [Fact]
    public async Task EscapingResolvedDirectoryIsCountedWithoutEnumeratingItsChildren()
    {
        var escapeDisplay = @"C:\Resolved\Photos\Escape";
        var escapeResolved = @"C:\Outside";
        var native = LocalRoot()
            .AddDirectory(
                @"C:\Resolved\Photos",
                escapeDisplay,
                escapeResolved,
                FakeWindowsScopeNative.Identity(2),
                isReparsePoint: true)
            .AddFile(escapeResolved, @"C:\Outside\outside.jpg");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
        Assert.Equal(1, evidence.Restrictions.EscapingReparseTargets);
        Assert.Equal(0, evidence.Counts.Supported);
        Assert.DoesNotContain(escapeResolved, native.EnumeratedPaths);
    }

    [Theory]
    [InlineData(ScopeCloudState.Synchronized, ScopeCloudState.Local)]
    [InlineData(ScopeCloudState.Placeholder, ScopeCloudState.Local)]
    [InlineData(ScopeCloudState.Unknown, ScopeCloudState.Local)]
    [InlineData(ScopeCloudState.Local, ScopeCloudState.Synchronized)]
    [InlineData(ScopeCloudState.Local, ScopeCloudState.Placeholder)]
    [InlineData(ScopeCloudState.Local, ScopeCloudState.Unknown)]
    public async Task EscapingDirectoryCloudEvidenceIsIgnoredAfterContainmentFails(
        ScopeCloudState entryCloud,
        ScopeCloudState directoryCloud)
    {
        const string escapeDisplay = @"C:\Resolved\Photos\Escape";
        const string escapeResolved = @"C:\Outside";
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\inside.jpg")
            .AddDirectory(
                @"C:\Resolved\Photos",
                escapeDisplay,
                escapeResolved,
                FakeWindowsScopeNative.Identity(2),
                entryCloud,
                directoryCloud,
                isReparsePoint: true)
            .AddFile(escapeResolved, @"C:\Outside\outside.jpg");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);
        var result = ScopePreflightPolicy.Evaluate(evidence);

        Assert.Equal(ScopeCloudState.Local, evidence.Cloud);
        Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
        Assert.Equal(new ScopeOutcomeCounts(1, 0, 0, 0), evidence.Counts);
        Assert.Equal(1, evidence.Restrictions.EscapingReparseTargets);
        Assert.Equal(ScopePreflightDecision.Ready, result.Decision);
        Assert.Contains(escapeDisplay, native.InspectedPaths);
        Assert.DoesNotContain(escapeResolved, native.EnumeratedPaths);
    }

    [Fact]
    public async Task ContainedAliasesEnumerateOneNativeDirectoryIdentity()
    {
        var firstDisplay = @"C:\Resolved\Photos\Album";
        var aliasDisplay = @"C:\Resolved\Photos\AlbumAlias";
        var resolved = @"C:\Resolved\Photos\Album";
        var identity = FakeWindowsScopeNative.Identity(2);
        var native = LocalRoot()
            .AddDirectory(
                @"C:\Resolved\Photos",
                firstDisplay,
                resolved,
                identity)
            .AddDirectory(
                @"C:\Resolved\Photos",
                aliasDisplay,
                resolved,
                identity,
                isReparsePoint: true)
            .AddFile(resolved, @"C:\Resolved\Photos\Album\one.jpg");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(1, evidence.Restrictions.DuplicateDirectoryIdentities);
        Assert.Equal(1, evidence.Counts.Supported);
        Assert.Equal(1, native.EnumeratedPaths.Count(path =>
            string.Equals(path, resolved, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DirectoryIdentityCycleDoesNotReenterTheRoot()
    {
        var child = @"C:\Resolved\Photos\Child";
        var cycle = @"C:\Resolved\Photos\Child\Back";
        var native = LocalRoot()
            .AddDirectory(
                @"C:\Resolved\Photos",
                child,
                child,
                FakeWindowsScopeNative.Identity(2))
            .AddDirectory(
                child,
                cycle,
                @"C:\Resolved\Photos",
                FakeWindowsScopeNative.Identity(1),
                isReparsePoint: true);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(1, evidence.Restrictions.DuplicateDirectoryIdentities);
        Assert.Equal(1, native.EnumeratedPaths.Count(path =>
            string.Equals(path, @"C:\Resolved\Photos", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ReadableRecognizedFilesEachIncrementSupportedExactlyOnce()
    {
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\one.JPEG")
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\two.png")
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\three.HeIc");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(new ScopeOutcomeCounts(3, 0, 0, 0), evidence.Counts);
        Assert.Equal(3, Total(evidence.Counts));
    }

    [Fact]
    public async Task MissingHeicCodecIsCachedAndEachHeicHasOneUnavailableOutcome()
    {
        var native = LocalRoot()
            .SetCodec(SupportedImageFormat.Heic, available: false)
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\one.heic")
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\two.HEIC");
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(new ScopeOutcomeCounts(0, 0, 0, 2), evidence.Counts);
        Assert.Equal(new[] { SupportedImageFormat.Heic }, native.CodecQueries);
        Assert.Empty(native.ReadProbePaths);
        Assert.Empty(native.ReadOpenPaths);
        Assert.Equal(2, Total(evidence.Counts));
    }

    [Fact]
    public async Task UnsupportedExtensionIsCountedWithoutCodecOrReadProbe()
    {
        var path = @"C:\Resolved\Photos\notes.txt";
        var native = LocalRoot().AddFile(@"C:\Resolved\Photos", path);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(new ScopeOutcomeCounts(0, 1, 0, 0), evidence.Counts);
        Assert.Empty(native.CodecQueries);
        Assert.Empty(native.ReadProbePaths);
        Assert.Empty(native.ReadOpenPaths);
    }

    [Fact]
    public async Task RecognizedFileReparsePointIsUnreadableWithoutCodecOrReadProbe()
    {
        const string path = @"C:\Resolved\Photos\escape.jpg";
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            path,
            isReparsePoint: true);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(new ScopeOutcomeCounts(0, 0, 1, 0), evidence.Counts);
        Assert.Empty(native.CodecQueries);
        Assert.Empty(native.ReadProbePaths);
        Assert.Empty(native.ReadOpenPaths);
    }

    [Fact]
    public async Task UnrecognizedFileReparsePointRemainsUnsupportedWithoutReadProbe()
    {
        const string path = @"C:\Resolved\Photos\escape.txt";
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            path,
            isReparsePoint: true);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(new ScopeOutcomeCounts(0, 1, 0, 0), evidence.Counts);
        Assert.Empty(native.CodecQueries);
        Assert.Empty(native.ReadProbePaths);
        Assert.Empty(native.ReadOpenPaths);
    }

    [Fact]
    public async Task IndividuallyUnreadableJpegHasOnlyUnreadableOutcome()
    {
        var path = @"C:\Resolved\Photos\locked.jpg";
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            path,
            canRead: false);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(new ScopeOutcomeCounts(0, 0, 1, 0), evidence.Counts);
        Assert.Equal(1, Total(evidence.Counts));
    }

    [Fact]
    public async Task FileReclassifiedAsSynchronizedBlocksWithoutReadOpenOrCompleteCounts()
    {
        const string path = @"C:\Resolved\Photos\changed.jpg";
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            path,
            cloud: ScopeCloudState.Local,
            canRead: true,
            readCloud: ScopeCloudState.Synchronized);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);
        var result = ScopePreflightPolicy.Evaluate(evidence);

        Assert.Equal(ScopeCloudState.Synchronized, evidence.Cloud);
        Assert.Equal(ScopeTraversalState.NotRun, evidence.Traversal);
        Assert.Null(result.CompleteCounts);
        Assert.Equal(path, Assert.Single(native.ReadProbePaths));
        Assert.Empty(native.ReadOpenPaths);
    }

    [Theory]
    [InlineData(ScopeCloudState.Synchronized)]
    [InlineData(ScopeCloudState.Placeholder)]
    [InlineData(ScopeCloudState.Unknown)]
    public async Task UnsafeEntryCloudStateStopsTraversalWithoutCompleteCounts(
        ScopeCloudState cloud)
    {
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\local.jpg")
            .AddFile(
                @"C:\Resolved\Photos",
                @"C:\Resolved\Photos\unsafe.jpg",
                cloud);
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);
        var result = ScopePreflightPolicy.Evaluate(evidence);

        Assert.Equal(cloud, evidence.Cloud);
        Assert.Equal(ScopeTraversalState.NotRun, evidence.Traversal);
        Assert.Null(result.CompleteCounts);
    }

    [Fact]
    public async Task NonAccessNativeFailureReturnsUnknownWithoutCompleteCounts()
    {
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", @"C:\Resolved\Photos\local.jpg")
            .FailEnumeration(
                @"C:\Resolved\Photos",
                new IOException("fixture drive disappearance"));
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(ScopeTraversalState.Unknown, evidence.Traversal);
        Assert.Null(ScopePreflightPolicy.Evaluate(evidence).CompleteCounts);
    }

    [Fact]
    public async Task UnknownFileReadProbeFailureReturnsUnknownWithoutCompleteCounts()
    {
        const string path = @"C:\Resolved\Photos\unknown.jpg";
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", path)
            .FailReadProbe(path, new IOException("fixture read-probe uncertainty"));
        var adapter = new WindowsLocalScopePreflight(native);

        var evidence = await adapter.InspectAsync(
            Selection(ScopeRecursion.IncludeSubfolders),
            CancellationToken.None);

        Assert.Equal(ScopeTraversalState.Unknown, evidence.Traversal);
        Assert.Equal(new ScopeOutcomeCounts(0, 0, 0, 0), evidence.Counts);
        Assert.Null(ScopePreflightPolicy.Evaluate(evidence).CompleteCounts);
    }

    [Fact]
    public async Task CancellationIsPropagatedInsteadOfBecomingUnknownEvidence()
    {
        var adapter = new WindowsLocalScopePreflight(LocalRoot());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.InspectAsync(
                Selection(ScopeRecursion.IncludeSubfolders),
                cancellation.Token));
    }

    private static FakeWindowsScopeNative LocalRoot() =>
        FakeWindowsScopeNative.ForRoot(ScopeVolumeKind.Fixed, ScopeCloudState.Local);

    private static ScopeSelection Selection(ScopeRecursion recursion) =>
        new(@"C:\Photos", recursion);

    private static int Total(ScopeOutcomeCounts counts) =>
        counts.Supported +
        counts.UnsupportedExtension +
        counts.Unreadable +
        counts.CodecUnavailable;
}
