using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;
using Xunit;

namespace Albumen.Infrastructure.Tests.Scope;

public sealed class WindowsTraversalProtectionTests
{
    private const string Root = @"C:\Resolved\Photos";
    private static readonly ScopeSelection Selection = new(@"C:\Photos", ScopeRecursion.IncludeSubfolders);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootReplacementAfterRootEvidenceCannotCompleteEvenWithoutEligibleFiles(bool unsupportedOnly)
    {
        var native = RootNative(unsupportedOnly);
        using var iterator = new WindowsScopeTraversal(native).Walk(Selection, TestContext.Current.CancellationToken).GetEnumerator();
        Assert.True(iterator.MoveNext());
        Assert.IsType<WindowsTraversalRoot>(iterator.Current);

        native.ReplaceDirectory(Selection.SelectedPath, FakeWindowsScopeNative.Identity(999));
        var remaining = Drain(iterator);

        var evidence = Assert.Single(remaining.OfType<WindowsTraversalFinished>()).Evidence;
        Assert.NotEqual(ScopeTraversalState.Complete, evidence.Traversal);
        Assert.Null(ScopePreflightPolicy.Evaluate(evidence).CompleteCounts);
        Assert.Empty(remaining.OfType<WindowsTraversalFile>());
        Assert.Empty(native.EnumeratedPaths);
        Assert.Empty(native.ReadProbePaths);
    }

    [Fact]
    public void QueuedChildRetargetIsRejectedBeforeOutsideBackedPathIsEnumerated()
    {
        const string child = Root + @"\queued";
        var native = RootNative(false)
            .AddDirectory(Root, child, child, FakeWindowsScopeNative.Identity(2))
            .AddFile(Root, Root + @"\pause.gif");
        using var iterator = new WindowsScopeTraversal(native).Walk(Selection, TestContext.Current.CancellationToken).GetEnumerator();
        Assert.True(iterator.MoveNext());
        Assert.IsType<WindowsTraversalRoot>(iterator.Current);
        Assert.True(iterator.MoveNext());
        Assert.IsType<WindowsTraversalFile>(iterator.Current);
        Assert.Contains(child, native.InspectedPaths);

        // The queued directory name now addresses an outside target. Native
        // pathname enumeration would still return names under that lookup path.
        native.ReplaceDirectory(child, FakeWindowsScopeNative.Identity(999), @"C:\Outside");
        native.AddFile(child, child + @"\outside.gif");
        var remaining = Drain(iterator);

        Assert.Empty(remaining.OfType<WindowsTraversalFile>());
        Assert.DoesNotContain(child, native.EnumeratedPaths);
        Assert.NotEqual(ScopeTraversalState.Complete,
            Assert.Single(remaining.OfType<WindowsTraversalFinished>()).Evidence.Traversal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanCannotPublishSuccessfulCoverageForRootReplacedAfterInspection(bool unsupportedOnly)
    {
        var native = RootNative(unsupportedOnly);
        ReplaceAfterInitialInspection(native);
        var context = new ScanRunContext(new ScanPin(Guid.NewGuid(), 1, 1, 1),
            new ResolvedScope(Selection, Root, FakeWindowsScopeNative.Identity(1)));
        var events = new List<ScanEnumerationEvent>();
        await foreach (var item in new WindowsScanEnumerator(native).EnumerateAsync(context, TestContext.Current.CancellationToken))
            events.Add(item);

        Assert.NotEqual(ScanStageState.Succeeded, Assert.Single(events.OfType<ScanEnumerationEnded>()).Outcome.State);
        Assert.Empty(events.OfType<ScanFileFound>());
        Assert.Empty(native.EnumeratedPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreflightCannotPublishCompleteCountsForRootReplacedAfterInspection(bool unsupportedOnly)
    {
        var native = RootNative(unsupportedOnly);
        ReplaceAfterInitialInspection(native);
        var evidence = await new WindowsLocalScopePreflight(native)
            .InspectAsync(Selection, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotEqual(ScopeTraversalState.Complete, evidence.Traversal);
        Assert.Null(ScopePreflightPolicy.Evaluate(evidence).CompleteCounts);
        Assert.Empty(native.EnumeratedPaths);
    }

    private static FakeWindowsScopeNative RootNative(bool unsupportedOnly)
    {
        var native = FakeWindowsScopeNative.ForRoot(ScopeVolumeKind.Fixed, ScopeCloudState.Local);
        if (unsupportedOnly) native.AddFile(Root, Root + @"\notes.gif");
        return native;
    }

    private static void ReplaceAfterInitialInspection(FakeWindowsScopeNative native)
    {
        native.AfterDirectoryInspection = path =>
        {
            native.AfterDirectoryInspection = null;
            native.ReplaceDirectory(path, FakeWindowsScopeNative.Identity(999));
        };
    }

    private static List<WindowsTraversalEvent> Drain(IEnumerator<WindowsTraversalEvent> iterator)
    {
        var result = new List<WindowsTraversalEvent>();
        while (iterator.MoveNext()) result.Add(iterator.Current);
        return result;
    }

    [Fact]
    public void ManyQueuedDirectoriesRetainOnlyRootAndCurrentProtection()
    {
        var native = RootNative(false);
        for (var index = 0; index < 100; index++)
            native.AddDirectory(Root, Root + $@"\child-{index}", Root + $@"\child-{index}", FakeWindowsScopeNative.Identity((ulong)index + 2));
        using var iterator = new WindowsScopeTraversal(native).Walk(Selection, TestContext.Current.CancellationToken).GetEnumerator();
        var events = Drain(iterator);

        Assert.Equal(ScopeTraversalState.Complete, Assert.Single(events.OfType<WindowsTraversalFinished>()).Evidence.Traversal);
        Assert.Equal(2, native.MaximumDirectoryProtections);
        Assert.Equal(0, native.LiveDirectoryProtections);
    }
}
