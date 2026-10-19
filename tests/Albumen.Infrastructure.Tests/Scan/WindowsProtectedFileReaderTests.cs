using Microsoft.Win32.SafeHandles;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scan;
using Xunit;

namespace Albumen.Infrastructure.Tests.Scan;

public sealed class WindowsProtectedFileReaderTests
{
    [Fact]
    public async Task NativeLeaseReadsUnicodeLongPathAndDistinguishesHardLinksFromCopies()
    {
        using var fixture = new ProtectedFileFixture();
        var directory = string.Join(Path.DirectorySeparatorChar,
            Enumerable.Range(0, 4).Select(index => $"segment-{index}-{new string('x', 58)}"));
        var path = fixture.CreateFile(Path.Combine(directory, "רחוב-日本語.jpg"));
        Assert.True(path.Length > 260);
        var alias = fixture.HardLink("alias.jpg", path);
        var copy = fixture.Copy("copy.jpg", path);
        var reader = new WindowsProtectedFileReader();
        await using var first = await reader.OpenAsync(fixture.Request(path), CancellationToken.None);
        await using var second = await reader.OpenAsync(fixture.Request(alias), CancellationToken.None);
        await using var third = await reader.OpenAsync(fixture.Request(copy), CancellationToken.None);

        Assert.Equal(first.OpenedFacts.PhysicalFileKey, second.OpenedFacts.PhysicalFileKey);
        Assert.NotEqual(first.OpenedFacts.PhysicalFileKey, third.OpenedFacts.PhysicalFileKey);
        Assert.Equal(fixture.Key(path), first.OpenedFacts.PhysicalFileKey);
        Assert.NotEqual(0UL, first.OpenedFacts.PhysicalFileKey.VolumeSerial);
        Assert.Equal(2u, first.OpenedFacts.Allocation.LinkCount);
        Assert.NotNull(first.OpenedFacts.Allocation.AllocatedBytes);
        Assert.Equal(7, first.OpenedFacts.ByteLength);
        Assert.False(first.Content.CanWrite);
        using var text = new StreamReader(first.Content, leaveOpen: true);
        Assert.Equal("fixture", await text.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(first.OpenedFacts.PhysicalFileKey,
            (await first.RefreshAsync(CancellationToken.None)).PhysicalFileKey);
    }

    [Fact]
    public async Task PreservesBothFileIdHalvesAndVolumeWithoutFormatting()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("halves.jpg");
        var key = new PhysicalFileKey(0x1122334455667788UL,
            new FileId128(0xFFEEDDCCBBAA9988UL, 0x1020304050607080UL));
        var source = new AlteredFileFacts((candidate, facts) => candidate == path
            ? facts with { PhysicalFileKey = key } : facts);
        await using var lease = await new WindowsProtectedFileReader(source).OpenAsync(
            fixture.Request(path) with { ExpectedPhysicalFileKey = key }, CancellationToken.None);
        Assert.Equal(key, lease.OpenedFacts.PhysicalFileKey);
    }

    [Fact]
    public async Task ExpectedIdentityMismatchIsChangedAndFailedOpenReleasesEveryHandle()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("changed.jpg");
        var request = fixture.Request(path) with
        {
            ExpectedPhysicalFileKey = new PhysicalFileKey(1, new FileId128(2, 3)),
        };
        await AssertFailure(new WindowsProtectedFileReader(), request,
            ScanStageState.FailedRetryable, ScanReason.Changed);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task ChangedApprovedRootIsRejectedBeforeContentIsExposed()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("root.jpg");
        var request = fixture.Request(path);
        fixture.ReplaceRoot();
        await AssertFailure(new WindowsProtectedFileReader(), request,
            ScanStageState.FailedRetryable, ScanReason.Changed);
    }

    [Fact]
    public async Task NativeFileReparsePointIsRefused()
    {
        using var fixture = new ProtectedFileFixture();
        var target = fixture.CreateFile("target.jpg");
        var link = fixture.PathFor("link.jpg");
        File.CreateSymbolicLink(link, target);
        await AssertFailure(new WindowsProtectedFileReader(), fixture.Request(link),
            ScanStageState.FailedTerminal, ScanReason.UnsafeLocation);
    }

    [Fact]
    public async Task TopLevelScopeCannotOpenARecursiveCandidate()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("album", "nested.jpg"));
        await AssertFailure(new WindowsProtectedFileReader(),
            fixture.Request(path, ScopeRecursion.TopLevelOnly),
            ScanStageState.FailedTerminal, ScanReason.UnsafeLocation);
    }

    [Fact]
    public async Task DirectoryAndNamedStreamCannotBeReadAsUnnamedFileContent()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("album", "source.jpg"));
        await AssertFailure(new WindowsProtectedFileReader(), fixture.Request(Path.GetDirectoryName(path)!),
            ScanStageState.FailedTerminal, ScanReason.UnsafeLocation);
        var namedStream = path + ":hidden";
        File.WriteAllText(namedStream, "not unnamed content");
        await AssertFailure(new WindowsProtectedFileReader(), fixture.Request(namedStream),
            ScanStageState.FailedTerminal, ScanReason.UnsafeLocation);
    }

    [Fact]
    public async Task MissingAndExistingWriterHaveDistinctRetryableReasons()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("busy.jpg");
        var request = fixture.Request(path);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        {
            await AssertFailure(new WindowsProtectedFileReader(), request,
                ScanStageState.FailedRetryable, ScanReason.SharingViolation);
        }

        File.Delete(path);
        await AssertFailure(new WindowsProtectedFileReader(), request,
            ScanStageState.FailedRetryable, ScanReason.Missing);
    }

    [Theory]
    [InlineData("identity", ScanReason.UnknownSafety)]
    [InlineData("length", ScanReason.UnknownSafety)]
    [InlineData("attributes", ScanReason.UnknownSafety)]
    [InlineData("deleteUnknown", ScanReason.UnknownSafety)]
    [InlineData("directoryUnknown", ScanReason.UnknownSafety)]
    [InlineData("cloudUnknown", ScanReason.UnknownSafety)]
    [InlineData("volumeUnknown", ScanReason.UnknownSafety)]
    [InlineData("pathUnknown", ScanReason.UnknownSafety)]
    [InlineData("directory", ScanReason.UnsafeLocation)]
    [InlineData("reparse", ScanReason.UnsafeLocation)]
    [InlineData("cloud", ScanReason.UnsafeLocation)]
    [InlineData("sync", ScanReason.UnsafeLocation)]
    [InlineData("remote", ScanReason.UnsafeLocation)]
    [InlineData("escape", ScanReason.UnsafeLocation)]
    public async Task UnknownOrUnsafeOpenedFactsNeverExposeContent(string change, ScanReason reason)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("unsafe.jpg");
        var source = new AlteredFileFacts((candidate, facts) => candidate == path
            ? Alter(facts, change, fixture.ContainerPath) : facts);
        await AssertFailure(new WindowsProtectedFileReader(source), fixture.Request(path),
            ScanStageState.FailedTerminal, reason);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Theory]
    [InlineData("identityChanged", ScanStageState.FailedRetryable, ScanReason.Changed)]
    [InlineData("lengthChanged", ScanStageState.FailedRetryable, ScanReason.Changed)]
    [InlineData("delete", ScanStageState.FailedRetryable, ScanReason.Changed)]
    [InlineData("cloudUnknown", ScanStageState.FailedTerminal, ScanReason.UnknownSafety)]
    [InlineData("escape", ScanStageState.FailedTerminal, ScanReason.UnsafeLocation)]
    public async Task RefreshRejectsChangedOrUnknownContentFacts(
        string change, ScanStageState state, ScanReason reason)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("refresh.jpg");
        var changed = false;
        var source = new AlteredFileFacts((candidate, facts) => changed && candidate == path
            ? Alter(facts, change, fixture.ContainerPath) : facts);
        await using var lease = await new WindowsProtectedFileReader(source).OpenAsync(
            fixture.Request(path), CancellationToken.None);
        changed = true;
        var failure = await Assert.ThrowsAsync<ScanReadException>(async () =>
            await lease.RefreshAsync(CancellationToken.None));
        Assert.Equal(new StageOutcome(state, reason), failure.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryMetadataChangeOrUnavailabilityDoesNotInvalidateProtectedContent(bool unknown)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("metadata.jpg");
        var changed = false;
        var source = new AlteredFileFacts((candidate, facts) => changed && candidate == path
            ? facts with
            {
                Allocation = unknown ? new AllocationEvidence(null, null, false)
                    : new AllocationEvidence(987654, 17, true),
                LastWriteUtc = DateTimeOffset.UnixEpoch,
            } : facts);
        await using var lease = await new WindowsProtectedFileReader(source).OpenAsync(
            fixture.Request(path), CancellationToken.None);
        changed = true;
        var final = await lease.RefreshAsync(CancellationToken.None);
        Assert.Equal(lease.OpenedFacts.PhysicalFileKey, final.PhysicalFileKey);
        Assert.Equal(lease.OpenedFacts.ByteLength, final.ByteLength);
        Assert.False(final.Allocation.Stable);
        using var text = new StreamReader(lease.Content, leaveOpen: true);
        Assert.Equal("fixture", await text.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownRecoveryFactsAtOpenRemainEligibleAndCancellationDisposalReleaseProtection()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("unknown-allocation.jpg");
        var source = new AlteredFileFacts((candidate, facts) => candidate == path
            ? facts with { Allocation = new AllocationEvidence(null, null, false), LastWriteUtc = null }
            : facts);
        var reader = new WindowsProtectedFileReader(source);
        var lease = await reader.OpenAsync(fixture.Request(path), CancellationToken.None);
        Assert.Null(lease.OpenedFacts.Allocation.AllocatedBytes);
        Assert.Null(lease.OpenedFacts.Allocation.LinkCount);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await lease.RefreshAsync(cancellation.Token));
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.False(lease.Content.CanRead);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Theory]
    [InlineData("cloud", ScanReason.UnsafeLocation)]
    [InlineData("cloudUnknown", ScanReason.UnknownSafety)]
    [InlineData("identityChanged", ScanReason.Changed)]
    public async Task RefreshRevalidatesTheApprovedAncestorChain(string change, ScanReason reason)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("album", "ancestor.jpg"));
        var ancestor = Path.GetDirectoryName(path)!;
        var changed = false;
        var source = new AlteredFileFacts((candidate, facts) => changed && candidate == ancestor
            ? Alter(facts, change, fixture.ContainerPath) : facts);
        await using var lease = await new WindowsProtectedFileReader(source).OpenAsync(
            fixture.Request(path), CancellationToken.None);
        changed = true;
        var failure = await Assert.ThrowsAsync<ScanReadException>(async () =>
            await lease.RefreshAsync(CancellationToken.None));
        Assert.Equal(reason, failure.Outcome.Reason);
    }

    internal static async Task AssertFailure(WindowsProtectedFileReader reader,
        ProtectedOpenRequest request, ScanStageState state, ScanReason reason)
    {
        var failure = await Assert.ThrowsAsync<ScanReadException>(async () =>
        {
            await using var lease = await reader.OpenAsync(request, CancellationToken.None);
        });
        Assert.Equal(new StageOutcome(state, reason), failure.Outcome);
    }

    private static WindowsFileObservation Alter(WindowsFileObservation facts, string change, string outside) =>
        change switch
        {
            "identity" => facts with { PhysicalFileKey = null },
            "identityChanged" => facts with { PhysicalFileKey = new PhysicalFileKey(1, new FileId128(2, 3)) },
            "length" => facts with { ByteLength = null },
            "lengthChanged" => facts with { ByteLength = 12345 },
            "attributes" => facts with { Attributes = null },
            "deleteUnknown" => facts with { DeletePending = null },
            "directoryUnknown" => facts with { IsDirectory = null },
            "cloudUnknown" => facts with { CloudState = ScopeCloudState.Unknown },
            "volumeUnknown" => facts with { VolumeKind = ScopeVolumeKind.Unknown },
            "pathUnknown" => facts with { ResolvedPath = null },
            "directory" => facts with { IsDirectory = true },
            "delete" => facts with { DeletePending = true },
            "reparse" => facts with { Attributes = facts.Attributes | 0x400u },
            "cloud" => facts with { CloudState = ScopeCloudState.Placeholder },
            "sync" => facts with { CloudState = ScopeCloudState.Synchronized },
            "remote" => facts with { VolumeKind = ScopeVolumeKind.Remote },
            "escape" => facts with { ResolvedPath = Path.Combine(outside, "escape.jpg") },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
}

internal sealed class AlteredFileFacts(Func<string, WindowsFileObservation, WindowsFileObservation> alter)
    : IWindowsFileFactsSource
{
    private readonly WindowsFileNative native = new();

    public WindowsFileObservation Read(SafeFileHandle handle, string path) =>
        alter(path, native.Read(handle, path));
}
