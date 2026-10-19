using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Tests.Scan;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;
using Xunit;

namespace Albumen.Infrastructure.Tests.Scope;

public sealed class WindowsDirectoryProtectionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraversalRetainsRootAndAncestorAgainstRenameUntilEarlyDisposal(bool renameAncestor)
    {
        using var fixture = new DirectoryFixture();
        var target = renameAncestor ? fixture.Ancestor : fixture.Root;
        var renamed = Path.Combine(fixture.Container, "renamed");
        var iterator = new WindowsScopeTraversal(fixture.Native).Walk(fixture.Selection, TestContext.Current.CancellationToken).GetEnumerator();
        try
        {
            Assert.True(iterator.MoveNext());
            Assert.IsType<WindowsTraversalRoot>(iterator.Current);
            var failure = Record.Exception(() => Directory.Move(target, renamed));
            Assert.True(failure is IOException or UnauthorizedAccessException, $"Expected retained directory protection; got {failure}.");
        }
        finally { iterator.Dispose(); }

        Directory.Move(target, renamed);
        Assert.True(Directory.Exists(renamed));
    }

    [Fact]
    public void ContainedAliasAndItsTargetCannotBeRetargetedWhileDirectoryLeaseIsLive()
    {
        using var fixture = new DirectoryFixture();
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "actual")).FullName;
        var alias = Path.Combine(fixture.Root, "alias");
        File.WriteAllText(Path.Combine(target, "inside.gif"), "fixture");
        Directory.CreateSymbolicLink(alias, target);
        try
        {
            using (var protection = fixture.Native.ProtectDirectory(fixture.Scope,
                fixture.Native.InspectDirectory(alias), alias, TestContext.Current.CancellationToken))
            {
                var failure = Record.Exception(() => Directory.Delete(alias));
                Assert.True(failure is IOException or UnauthorizedAccessException);
                failure = Record.Exception(() => Directory.Move(target, Path.Combine(fixture.Root, "moved")));
                Assert.True(failure is IOException or UnauthorizedAccessException);
                Assert.Equal("inside.gif", Path.GetFileName(Assert.Single(protection.Enumerate(TestContext.Current.CancellationToken)).Path));
            }
            Directory.Delete(alias);
            Directory.Move(target, Path.Combine(fixture.Root, "moved"));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
        }
    }

    [Fact]
    public void NativeQueuedDirectoryReplacementIsRejectedBeforeOutsideNamesAreExposed()
    {
        using var fixture = new DirectoryFixture();
        var child = Directory.CreateDirectory(Path.Combine(fixture.Root, "queued")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Container, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "outside.gif"), "fixture");
        var changed = false;
        var native = new MutationNative(fixture.Native)
        {
            AfterInspection = path =>
            {
                if (path != child || changed) return;
                changed = true;
                Directory.Move(child, Path.Combine(fixture.Root, "old-child"));
                Directory.CreateSymbolicLink(child, outside);
            },
        };
        try
        {
            var events = new WindowsScopeTraversal(native).Walk(fixture.Selection, TestContext.Current.CancellationToken).ToArray();
            Assert.True(changed);
            Assert.Empty(events.OfType<WindowsTraversalFile>());
            Assert.NotEqual(ScopeTraversalState.Complete, Assert.Single(events.OfType<WindowsTraversalFinished>()).Evidence.Traversal);
        }
        finally
        {
            if (changed && Directory.Exists(child)) Directory.Delete(child);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unsupported")]
    [InlineData("cancel")]
    [InlineData("failure")]
    public void CompletionCancellationAndFailureReleaseDirectoryProtection(string ending)
    {
        using var fixture = new DirectoryFixture();
        if (ending == "unsupported") File.WriteAllText(Path.Combine(fixture.Root, "notes.gif"), "fixture");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var changed = false;
        var source = new AlteredFileFacts((path, facts) => changed && path == fixture.Root
            ? facts with { CloudState = ScopeCloudState.Placeholder } : facts);
        var native = new WindowsScopeNative(new WicCodecCapability(new ComClassAvailability()), new WindowsFileReadProbe(), source);
        using (var iterator = new WindowsScopeTraversal(native).Walk(fixture.Selection, cancellation.Token).GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            Assert.IsType<WindowsTraversalRoot>(iterator.Current);
            if (ending == "cancel")
            {
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => iterator.MoveNext());
            }
            else
            {
                changed = ending == "failure";
                var remaining = new List<WindowsTraversalEvent>();
                while (iterator.MoveNext()) remaining.Add(iterator.Current);
                var evidence = Assert.Single(remaining.OfType<WindowsTraversalFinished>()).Evidence;
                if (changed) Assert.NotEqual(ScopeTraversalState.Complete, evidence.Traversal);
                else Assert.Equal(ScopeTraversalState.Complete, evidence.Traversal);
            }
        }
        Directory.Move(fixture.Root, Path.Combine(fixture.Container, "released"));
    }

    [Fact]
    public void HandleBoundListingUsesMultipleBoundedBatchesWithoutMissingOrDuplicatingEntries()
    {
        using var fixture = new DirectoryFixture();
        for (var index = 0; index < 600; index++)
            File.WriteAllText(Path.Combine(fixture.Root, $"{index:D4}-{new string('x', 100)}.gif"), "");
        using var protection = fixture.Native.ProtectDirectory(fixture.Scope,
            fixture.Native.InspectDirectory(fixture.Root), fixture.Root, TestContext.Current.CancellationToken);

        var entries = protection.Enumerate(TestContext.Current.CancellationToken).ToArray();

        Assert.Equal(600, entries.Length);
        Assert.Equal(600, entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(entries, entry => Assert.Equal(ScopeCloudState.Local, entry.CloudState));
    }

    private sealed class DirectoryFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("AlbumenDirectoryGuard-");
        public DirectoryFixture() => Directory.CreateDirectory(Root);
        public string Container => directory.FullName;
        public string Ancestor => Path.Combine(Container, "ancestor");
        public string Root => Path.Combine(Ancestor, "scope");
        public ScopeSelection Selection => new(Root, ScopeRecursion.IncludeSubfolders);
        public ResolvedScope Scope => new(Selection, Root, Native.InspectDirectory(Root).Identity);
        public WindowsScopeNative Native { get; } = new(new WicCodecCapability(new ComClassAvailability()));
        public void Dispose() => directory.Delete(recursive: true);
    }

    private sealed class MutationNative(WindowsScopeNative inner) : IWindowsScopeNative
    {
        public Action<string>? AfterInspection { get; init; }
        public WindowsDirectoryFacts InspectDirectory(string path)
        {
            var facts = inner.InspectDirectory(path);
            AfterInspection?.Invoke(path);
            return facts;
        }
        public IWindowsDirectoryProtection ProtectDirectory(ResolvedScope scope, WindowsDirectoryFacts expected,
            string discoveredPath, CancellationToken cancellationToken) => inner.ProtectDirectory(scope, expected, discoveredPath, cancellationToken);
        public ScopeVolumeKind GetVolumeKind(string resolvedPath) => inner.GetVolumeKind(resolvedPath);
        public IEnumerable<WindowsScopeEntry> EnumerateDirectory(string resolvedPath) => inner.EnumerateDirectory(resolvedPath);
        public WindowsFileReadProbeResult ProbeFileForRead(string path) => inner.ProbeFileForRead(path);
        public PhysicalFileKey InspectFileIdentity(string path) => inner.InspectFileIdentity(path);
        public bool IsCodecAvailable(SupportedImageFormat format) => inner.IsCodecAvailable(format);
    }
}
