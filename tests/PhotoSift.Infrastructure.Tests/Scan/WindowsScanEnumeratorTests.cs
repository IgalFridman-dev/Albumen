using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Tests.Scope;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Scan;

public sealed class WindowsScanEnumeratorTests
{
    private static readonly PhysicalFileKey FirstKey =
        new(7, new FileId128(0, 101));
    private static readonly PhysicalFileKey SecondKey =
        new(7, new FileId128(0, 102));
    private static readonly PhysicalFileKey UnsupportedKey =
        new(7, new FileId128(0, 103));
    private static readonly PhysicalFileKey HeicKey =
        new(7, new FileId128(0, 104));

    [Theory]
    [InlineData("holiday.JPG", SupportedImageFormat.Jpeg)]
    [InlineData("holiday.jpeg", SupportedImageFormat.Jpeg)]
    [InlineData("רחוב.PNG", SupportedImageFormat.Png)]
    [InlineData("holiday.HeIc", SupportedImageFormat.Heic)]
    public void RecognizesOnlyExistingImageCapabilities(
        string path,
        SupportedImageFormat expected)
    {
        Assert.Equal(expected, WindowsScopeFilePolicy.Recognize(path));
        Assert.Null(WindowsScopeFilePolicy.Recognize("holiday.gif"));
    }

    [Fact]
    public async Task EmitsEachContainedPathOnceWithExplicitOutcomesAndOneTerminalEvent()
    {
        const string root = @"C:\Resolved\Photos";
        const string album = @"C:\Resolved\Photos\Album";
        const string alias = @"C:\Resolved\Photos\AlbumAlias";
        var albumIdentity = FakeWindowsScopeNative.Identity(2);
        var native = LocalRoot()
            .AddFile(root, root + @"\one.JPG", physicalFileKey: FirstKey)
            .AddFile(root, root + @"\notes.gif", physicalFileKey: UnsupportedKey)
            .AddFile(root, root + @"\missing.heic", physicalFileKey: HeicKey)
            .SetCodec(SupportedImageFormat.Heic, available: false)
            .AddDirectory(root, album, album, albumIdentity)
            .AddDirectory(root, alias, album, albumIdentity, isReparsePoint: true)
            .AddFile(album, album + @"\two.png", physicalFileKey: SecondKey)
            .AddDirectory(
                root,
                root + @"\Escape",
                @"C:\Outside",
                FakeWindowsScopeNative.Identity(3),
                isReparsePoint: true)
            .AddFile(@"C:\Outside", @"C:\Outside\outside.jpg");

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        var files = events.OfType<ScanFileFound>().Select(item => item.File).ToArray();
        Assert.Equal(4, files.Length);
        AssertFile(
            files,
            @"C:\Photos\one.JPG",
            root + @"\one.JPG",
            FirstKey,
            ScanStageState.Succeeded,
            ScanReason.None);
        AssertFile(
            files,
            @"C:\Photos\notes.gif",
            root + @"\notes.gif",
            expectedKey: null,
            ScanStageState.Unsupported,
            ScanReason.UnsupportedExtension);
        AssertFile(
            files,
            @"C:\Photos\missing.heic",
            root + @"\missing.heic",
            expectedKey: null,
            ScanStageState.Unsupported,
            ScanReason.CodecUnavailable);
        AssertFile(
            files,
            @"C:\Photos\Album\two.png",
            album + @"\two.png",
            SecondKey,
            ScanStageState.Succeeded,
            ScanReason.None);
        Assert.Equal(
            new[] { root, album },
            native.EnumeratedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.DoesNotContain(@"C:\Outside", native.EnumeratedPaths);
        Assert.Equal(2, native.InspectedFileIdentityPaths.Count);
        Assert.Equal(
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task FileIdentityFailureProducesAVisibleFailedRow()
    {
        const string path = @"C:\Resolved\Photos\denied.jpg";
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", path)
            .FailFileIdentity(path, new UnauthorizedAccessException("fixture denial"));

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        var file = Assert.Single(events.OfType<ScanFileFound>()).File;
        Assert.Equal(@"C:\Photos\denied.jpg", file.DiscoveredPath);
        Assert.Equal(path, file.ResolvedPath);
        Assert.Null(file.PhysicalFileKey);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
            file.Outcome);
        Assert.Equal(
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task UnknownFileIdentityFailureProducesAVisibleRowAndBlocksCoverage()
    {
        const string path = @"C:\Resolved\Photos\unknown.jpg";
        var native = LocalRoot()
            .AddFile(@"C:\Resolved\Photos", path)
            .FailFileIdentity(path, new IOException("fixture identity uncertainty"));

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        var file = Assert.Single(events.OfType<ScanFileFound>()).File;
        Assert.Null(file.PhysicalFileKey);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
            file.Outcome);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task RecognizedFileReparsePointIsAnExplicitUnsafeFailure()
    {
        const string path = @"C:\Resolved\Photos\linked.jpg";
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            path,
            isReparsePoint: true,
            physicalFileKey: FirstKey);

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        var file = Assert.Single(events.OfType<ScanFileFound>()).File;
        Assert.Equal(@"C:\Photos\linked.jpg", file.DiscoveredPath);
        Assert.Equal(path, file.ResolvedPath);
        Assert.Null(file.PhysicalFileKey);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation),
            file.Outcome);
        Assert.Empty(native.InspectedFileIdentityPaths);
        Assert.Empty(native.ReadProbePaths);
        Assert.Equal(
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task InaccessibleIncludedDirectoryEndsWithIncompleteEnumeration()
    {
        const string root = @"C:\Resolved\Photos";
        const string denied = @"C:\Resolved\Photos\Denied";
        var native = LocalRoot()
            .AddFile(root, root + @"\visible.jpg", physicalFileKey: FirstKey)
            .AddDirectory(
                root,
                denied,
                denied,
                FakeWindowsScopeNative.Identity(2))
            .FailEnumeration(denied, new UnauthorizedAccessException("fixture denial"));

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        Assert.Single(events.OfType<ScanFileFound>());
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Theory]
    [InlineData(ScopeCloudState.Synchronized, ScanReason.UnsafeLocation)]
    [InlineData(ScopeCloudState.Placeholder, ScanReason.UnsafeLocation)]
    [InlineData(ScopeCloudState.Unknown, ScanReason.UnknownSafety)]
    public async Task UnsafeEntryBlocksCoverageBeforeUnsupportedExtensionClassification(
        ScopeCloudState cloud,
        ScanReason reason)
    {
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            @"C:\Resolved\Photos\unsafe.gif",
            cloud);

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        var file = Assert.Single(events.OfType<ScanFileFound>()).File;
        Assert.Null(file.PhysicalFileKey);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedTerminal, reason),
            file.Outcome);
        Assert.Empty(native.InspectedFileIdentityPaths);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedTerminal, reason),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task UnknownTraversalFailureCannotReportCompleteInventory()
    {
        var native = LocalRoot().FailEnumeration(
            @"C:\Resolved\Photos",
            new IOException("fixture drive disappearance"));

        var events = await CollectAsync(new WindowsScanEnumerator(native), Context());

        Assert.Empty(events.OfType<ScanFileFound>());
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task TopLevelOnlyNeverInspectsAnInaccessibleChild()
    {
        const string child = @"C:\Resolved\Photos\Denied";
        var native = LocalRoot()
            .AddFile(
                @"C:\Resolved\Photos",
                @"C:\Resolved\Photos\top.jpg",
                physicalFileKey: FirstKey)
            .AddDirectory(
                @"C:\Resolved\Photos",
                child,
                child,
                FakeWindowsScopeNative.Identity(2))
            .FailInspection(child, new UnauthorizedAccessException("must remain out of scope"));

        var events = await CollectAsync(
            new WindowsScanEnumerator(native),
            Context(ScopeRecursion.TopLevelOnly));

        Assert.Single(events.OfType<ScanFileFound>());
        Assert.DoesNotContain(child, native.InspectedPaths);
        Assert.DoesNotContain(child, native.EnumeratedPaths);
        Assert.Equal(
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task ChangedRootIdentityEndsBeforeEligibleWorkIsEmitted()
    {
        const string path = @"C:\Resolved\Photos\one.jpg";
        var native = LocalRoot().AddFile(
            @"C:\Resolved\Photos",
            path,
            physicalFileKey: FirstKey);
        var staleContext = new ScanRunContext(
            Pin(),
            new ResolvedScope(
                new ScopeSelection(@"C:\Photos", ScopeRecursion.IncludeSubfolders),
                @"C:\Resolved\Photos",
                FakeWindowsScopeNative.Identity(999)));

        var events = await CollectAsync(new WindowsScanEnumerator(native), staleContext);

        Assert.Empty(events.OfType<ScanFileFound>());
        Assert.Empty(native.InspectedFileIdentityPaths);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed),
            Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome);
    }

    [Fact]
    public async Task CancellationProducesOneCanceledTerminalEvent()
    {
        const string root = @"C:\Resolved\Photos";
        var native = LocalRoot()
            .AddFile(root, root + @"\one.jpg", physicalFileKey: FirstKey)
            .AddFile(root, root + @"\two.jpg", physicalFileKey: SecondKey);
        using var cancellation = new CancellationTokenSource();
        await using var iterator = new WindowsScanEnumerator(native)
            .EnumerateAsync(Context(), cancellation.Token)
            .GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<ScanFileFound>(iterator.Current);
        cancellation.Cancel();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(
            new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled),
            Assert.IsType<ScanEnumerationEnded>(iterator.Current).Outcome);
        Assert.False(await iterator.MoveNextAsync());
    }

    private static async Task<ScanEnumerationEvent[]> CollectAsync(
        WindowsScanEnumerator enumerator,
        ScanRunContext context)
    {
        var events = new List<ScanEnumerationEvent>();
        await foreach (var item in enumerator.EnumerateAsync(
            context,
            TestContext.Current.CancellationToken))
        {
            events.Add(item);
        }

        return events.ToArray();
    }

    private static void AssertFile(
        IEnumerable<EnumeratedFile> files,
        string discoveredPath,
        string resolvedPath,
        PhysicalFileKey? expectedKey,
        ScanStageState state,
        ScanReason reason)
    {
        var file = Assert.Single(files, candidate =>
            string.Equals(candidate.ResolvedPath, resolvedPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(discoveredPath, file.DiscoveredPath);
        Assert.Equal(expectedKey, file.PhysicalFileKey);
        Assert.Equal(new StageOutcome(state, reason), file.Outcome);
    }

    private static FakeWindowsScopeNative LocalRoot() =>
        FakeWindowsScopeNative.ForRoot(ScopeVolumeKind.Fixed, ScopeCloudState.Local);

    private static ScanRunContext Context(
        ScopeRecursion recursion = ScopeRecursion.IncludeSubfolders) =>
        new(
            Pin(),
            new ResolvedScope(
                new ScopeSelection(@"C:\Photos", recursion),
                @"C:\Resolved\Photos",
                FakeWindowsScopeNative.Identity(1)));

    private static ScanPin Pin() =>
        new(new Guid("22222222-2222-2222-2222-222222222222"), 2, 3, 4);
}
