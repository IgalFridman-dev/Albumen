using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal interface IWindowsScopeNative
{
    WindowsDirectoryFacts InspectDirectory(string path);

    IWindowsDirectoryProtection ProtectDirectory(ResolvedScope scope, WindowsDirectoryFacts expected,
        string discoveredPath, CancellationToken cancellationToken);

    ScopeVolumeKind GetVolumeKind(string resolvedPath);

    IEnumerable<WindowsScopeEntry> EnumerateDirectory(string resolvedPath);

    WindowsFileReadProbeResult ProbeFileForRead(string path);

    PhysicalFileKey InspectFileIdentity(string path);

    bool IsCodecAvailable(SupportedImageFormat format);
}

internal interface IWindowsDirectoryProtection : IDisposable
{
    void Validate(CancellationToken cancellationToken);

    IEnumerable<WindowsScopeEntry> Enumerate(CancellationToken cancellationToken);
}

internal interface IComClassAvailability
{
    bool IsAvailable(Guid classId);
}

internal interface IWindowsFileReadProbe
{
    ScopeCloudState GetCloudState(string path);

    bool TryOpenForRead(string path);
}
