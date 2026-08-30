using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Scope;

public sealed class WindowsScopeNativeTests
{
    [Fact]
    public void InspectDirectoryReturnsFinalPathAndStableIdentityWithoutChangingCurrentDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("PhotoSiftNative-");
        var originalCurrentDirectory = Environment.CurrentDirectory;

        try
        {
            var native = new WindowsScopeNative(
                new WicCodecCapability(new ComClassAvailability()));

            var facts = native.InspectDirectory(directory.FullName);

            Assert.Equal(originalCurrentDirectory, Environment.CurrentDirectory);
            Assert.True(Path.IsPathFullyQualified(facts.ResolvedPath));
            Assert.True(Directory.Exists(facts.ResolvedPath));
            Assert.NotEqual(0UL, facts.Identity.VolumeSerialNumber);
            Assert.NotEqual(
                (0UL, 0UL),
                (facts.Identity.FileIdHigh, facts.Identity.FileIdLow));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0u, ScopeVolumeKind.Unknown)]
    [InlineData(1u, ScopeVolumeKind.Unavailable)]
    [InlineData(2u, ScopeVolumeKind.Removable)]
    [InlineData(3u, ScopeVolumeKind.Fixed)]
    [InlineData(4u, ScopeVolumeKind.Remote)]
    [InlineData(5u, ScopeVolumeKind.Unsupported)]
    [InlineData(6u, ScopeVolumeKind.Unsupported)]
    [InlineData(99u, ScopeVolumeKind.Unknown)]
    public void MapsDocumentedDriveTypes(uint driveType, ScopeVolumeKind expected)
    {
        Assert.Equal(expected, WindowsScopeNative.MapDriveType(driveType));
    }

    [Theory]
    [InlineData(0x00000080u, 0u, ScopeCloudState.Local)]
    [InlineData(0u, 1u, ScopeCloudState.Placeholder)]
    [InlineData(0u, 2u, ScopeCloudState.Synchronized)]
    [InlineData(0x00001000u, 0u, ScopeCloudState.Placeholder)]
    [InlineData(0x00040000u, 0u, ScopeCloudState.Placeholder)]
    [InlineData(0x00400000u, 0u, ScopeCloudState.Placeholder)]
    [InlineData(0u, uint.MaxValue, ScopeCloudState.Unknown)]
    public void MapsCloudEvidence(
        uint attributes,
        uint placeholderState,
        ScopeCloudState expected)
    {
        Assert.Equal(
            expected,
            WindowsScopeNative.MapCloudState(attributes, placeholderState));
    }

    [Fact]
    public void EnumerateDirectoryReturnsOneLevelLocalEntryFacts()
    {
        var directory = Directory.CreateTempSubdirectory("PhotoSiftEnumerate-");
        var childPath = Path.Combine(directory.FullName, "ordinary.jpg");
        File.WriteAllText(childPath, "not image pixels");

        try
        {
            var entry = Assert.Single(CreateNative().EnumerateDirectory(directory.FullName));

            Assert.Equal(childPath, entry.Path);
            Assert.False(entry.IsDirectory);
            Assert.False(entry.IsReparsePoint);
            Assert.Equal(ScopeCloudState.Local, entry.CloudState);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadProbeReturnsFalseForSharingViolation()
    {
        var path = Path.GetTempFileName();

        try
        {
            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            Assert.False(CreateNative().CanOpenForRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingDirectoryProducesTypedUnavailableFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        var exception = Assert.Throws<WindowsScopeNativeException>(
            () => CreateNative().InspectDirectory(path));

        Assert.Equal(WindowsScopeFailureKind.Unavailable, exception.Kind);
        Assert.Equal(path, exception.PathValue);
        Assert.NotEqual(0, exception.NativeError);
    }

    private static WindowsScopeNative CreateNative() =>
        new(new WicCodecCapability(new ComClassAvailability()));
}
