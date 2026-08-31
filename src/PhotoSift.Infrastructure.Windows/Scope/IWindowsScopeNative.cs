using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal interface IWindowsScopeNative
{
    WindowsDirectoryFacts InspectDirectory(string path);

    ScopeVolumeKind GetVolumeKind(string resolvedPath);

    IEnumerable<WindowsScopeEntry> EnumerateDirectory(string resolvedPath);

    WindowsFileReadProbeResult ProbeFileForRead(string path);

    bool IsCodecAvailable(SupportedImageFormat format);
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
