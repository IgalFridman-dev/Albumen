using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Albumen.Application.Action;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Action;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;
using Xunit;

namespace Albumen.Infrastructure.Tests.ActionExecution;

[Trait("Category", "GoldenVm")]
public sealed class WindowsActionRevalidationProbeTests
{
    [Fact]
    public async Task ExactIdentityLengthAndContentConfirmOriginalFilePresent()
    {
        using var fixture = new ActionFileFixture();
        var path = fixture.Create("confirmed.jpg", "reviewed-content");
        var request = Request(path);

        var result = await new WindowsActionRevalidationProbe().ProbeAsync(request, CancellationToken.None);

        Assert.Equal(ActionRevalidationKind.ConfirmedPresent, result.Kind);
        Assert.Equal(ActionRevalidationReason.OriginalFileConfirmed, result.Reason);
        Assert.Equal(new ActionRevalidationObservation(true, request.ReviewedKey,
            request.ReviewedLength, request.ReviewedSha256), result.Observation);
    }

    [Fact]
    public async Task MissingOriginalPathRemainsUnknownAndNeverBecomesRecycled()
    {
        using var fixture = new ActionFileFixture();
        var path = fixture.Create("missing.jpg");
        var request = Request(path);
        Assert.Equal(0, fixture.Delete(path));

        var result = await new WindowsActionRevalidationProbe().ProbeAsync(request, CancellationToken.None);

        Assert.Equal(ActionRevalidationKind.StillUnknown, result.Kind);
        Assert.Equal(ActionRevalidationReason.OriginalPathMissing, result.Reason);
        Assert.Equal(new ActionRevalidationObservation(false, null, null, null), result.Observation);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("length")]
    [InlineData("content")]
    [InlineData("finalIdentity")]
    [InlineData("finalLength")]
    public async Task ChangedOrReplacedFileRemainsUnknownWithObservedFacts(string change)
    {
        using var fixture = new ActionFileFixture();
        var path = fixture.Create("changed.jpg", "reviewed");
        var request = Request(path);
        var reads = 0;
        var native = new RevalidationNative((_, facts) =>
        {
            reads++;
            return change switch
            {
                "identity" => facts with { PhysicalFileKey = DifferentKey(request.ReviewedKey) },
                "length" => facts with { ByteLength = request.ReviewedLength + 1 },
                "finalIdentity" when reads == 2 => facts with { PhysicalFileKey = DifferentKey(request.ReviewedKey) },
                "finalLength" when reads == 2 => facts with { ByteLength = request.ReviewedLength + 1 },
                _ => facts,
            };
        });
        if (change == "content")
        {
            fixture.Write(path, "modified");
            Assert.Equal(request.ReviewedLength, new FileInfo(path).Length);
        }

        var result = await new WindowsActionRevalidationProbe(native).ProbeAsync(request, CancellationToken.None);

        Assert.Equal(ActionRevalidationKind.StillUnknown, result.Kind);
        Assert.Equal(ActionRevalidationReason.OriginalPathChanged, result.Reason);
        Assert.True(result.Observation.PathExists);
        Assert.NotNull(result.Observation.ObservedKey);
        Assert.NotNull(result.Observation.ObservedLength);
        Assert.NotEqual(ActionRevalidationReason.OriginalFileConfirmed, result.Reason);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    public async Task AccessOrSharingFailureRemainsUnknownWithoutInventingAbsence(int error)
    {
        using var fixture = new ActionFileFixture();
        var path = fixture.Create("unavailable.jpg");
        var request = Request(path);
        var native = new RevalidationNative((_, facts) => facts, error);

        var result = await new WindowsActionRevalidationProbe(native).ProbeAsync(request, CancellationToken.None);

        Assert.Equal(ActionRevalidationKind.StillUnknown, result.Kind);
        Assert.Equal(ActionRevalidationReason.AccessUnavailable, result.Reason);
        Assert.Equal(new ActionRevalidationObservation(true, null, null, null), result.Observation);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("cloud")]
    [InlineData("reparse")]
    [InlineData("directory")]
    [InlineData("escape")]
    public async Task UnsafeLocationRemainsUnknownAndIsNeverConfirmed(string change)
    {
        using var fixture = new ActionFileFixture();
        var path = fixture.Create("unsafe.jpg");
        var request = Request(path);
        var native = new RevalidationNative((_, facts) => change switch
        {
            "remote" => facts with { VolumeKind = ScopeVolumeKind.Remote },
            "cloud" => facts with { CloudState = ScopeCloudState.Placeholder },
            "reparse" => facts with { Attributes = facts.Attributes | 0x400u },
            "directory" => facts with { IsDirectory = true },
            "escape" => facts with { ResolvedPath = fixture.PathFor("other.jpg") },
            _ => facts,
        });

        var result = await new WindowsActionRevalidationProbe(native).ProbeAsync(request, CancellationToken.None);

        Assert.Equal(ActionRevalidationKind.StillUnknown, result.Kind);
        Assert.Equal(ActionRevalidationReason.LocationUnavailable, result.Reason);
        Assert.True(result.Observation.PathExists);
    }

    [Fact]
    public void NativeOpenAllowsOnlyReadsAndDeniesWriteDeleteAndDeleteSharing()
    {
        using var fixture = new ActionFileFixture();
        var path = fixture.Create("rights.jpg");
        var native = new WindowsActionRevalidationNative();
        using var held = native.OpenReadOnly(path);
        using var secondRead = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<IOException>(() =>
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.Equal(32, OpenDelete(path));
    }

    private static ActionRevalidationRequest Request(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var facts = new WindowsFileNative().Read(stream.SafeFileHandle, path);
        return new ActionRevalidationRequest(Guid.NewGuid(), path, facts.PhysicalFileKey!.Value,
            stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static PhysicalFileKey DifferentKey(PhysicalFileKey key) =>
        new(key.VolumeSerial, new FileId128(key.FileId.High, key.FileId.Low + 1));

    private static int OpenDelete(string path)
    {
        using var handle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(path), 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0);
        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    private sealed class RevalidationNative(
        Func<string, WindowsFileObservation, WindowsFileObservation> alter,
        int? openError = null) : IWindowsActionRevalidationNative
    {
        private readonly WindowsFileNative native = new();

        public SafeFileHandle OpenReadOnly(string path)
        {
            if (openError is { } error) throw WindowsFileNative.Failure(error, path);
            return new WindowsActionRevalidationNative().OpenReadOnly(path);
        }

        public WindowsFileObservation Read(SafeFileHandle handle, string path) =>
            alter(path, native.Read(handle, path));
    }
}
