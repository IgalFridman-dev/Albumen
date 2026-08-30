using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal interface IWindowsScopeNative
{
    WindowsDirectoryFacts InspectDirectory(string path);

    ScopeVolumeKind GetVolumeKind(string resolvedPath);

    IEnumerable<WindowsScopeEntry> EnumerateDirectory(string resolvedPath);

    bool CanOpenForRead(string path);

    bool IsCodecAvailable(SupportedImageFormat format);
}

internal interface IComClassAvailability
{
    bool IsAvailable(Guid classId);
}
