using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.Infrastructure.Tests.Scope;

internal sealed class FakeWindowsScopeNative : IWindowsScopeNative
{
    private readonly Dictionary<string, WindowsDirectoryFacts> directories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<WindowsScopeEntry>> entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> enumerationFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> inspectionFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> readable =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SupportedImageFormat, bool> codecs = [];
    private readonly ScopeVolumeKind volume;

    private FakeWindowsScopeNative(
        ScopeVolumeKind volume,
        string displayPath,
        string resolvedPath,
        ScopeCloudState cloud)
    {
        this.volume = volume;
        RootDisplayPath = displayPath;
        RootResolvedPath = resolvedPath;
        directories[displayPath] = new WindowsDirectoryFacts(
            displayPath,
            resolvedPath,
            Identity(1),
            IsReparsePoint: false,
            cloud);
    }

    public string RootDisplayPath { get; }

    public string RootResolvedPath { get; }

    public List<string> InspectedPaths { get; } = [];

    public List<string> VolumePaths { get; } = [];

    public List<string> EnumeratedPaths { get; } = [];

    public List<string> ReadPaths { get; } = [];

    public List<SupportedImageFormat> CodecQueries { get; } = [];

    public static FakeWindowsScopeNative ForRoot(
        ScopeVolumeKind volume,
        ScopeCloudState cloud,
        string displayPath = @"C:\Photos",
        string resolvedPath = @"C:\Resolved\Photos") =>
        new(volume, displayPath, resolvedPath, cloud);

    public static DirectoryIdentity Identity(ulong value) => new(7, 0, value);

    public FakeWindowsScopeNative AddFile(
        string directory,
        string path,
        ScopeCloudState cloud = ScopeCloudState.Local,
        bool canRead = true)
    {
        EntriesFor(directory).Add(new WindowsScopeEntry(
            path,
            IsDirectory: false,
            IsReparsePoint: false,
            cloud));
        readable[path] = canRead;
        return this;
    }

    public FakeWindowsScopeNative AddDirectory(
        string parent,
        string displayPath,
        string resolvedPath,
        DirectoryIdentity identity,
        ScopeCloudState entryCloud = ScopeCloudState.Local,
        ScopeCloudState directoryCloud = ScopeCloudState.Local,
        bool isReparsePoint = false)
    {
        EntriesFor(parent).Add(new WindowsScopeEntry(
            displayPath,
            IsDirectory: true,
            isReparsePoint,
            entryCloud));
        directories[displayPath] = new WindowsDirectoryFacts(
            displayPath,
            resolvedPath,
            identity,
            isReparsePoint,
            directoryCloud);
        return this;
    }

    public FakeWindowsScopeNative SetCodec(
        SupportedImageFormat format,
        bool available)
    {
        codecs[format] = available;
        return this;
    }

    public FakeWindowsScopeNative FailEnumeration(string path, Exception exception)
    {
        enumerationFailures[path] = exception;
        return this;
    }

    public FakeWindowsScopeNative FailInspection(string path, Exception exception)
    {
        inspectionFailures[path] = exception;
        return this;
    }

    public WindowsDirectoryFacts InspectDirectory(string path)
    {
        InspectedPaths.Add(path);
        if (inspectionFailures.TryGetValue(path, out var failure))
        {
            throw failure;
        }

        return directories.TryGetValue(path, out var facts)
            ? facts
            : throw new InvalidOperationException($"No directory facts for '{path}'.");
    }

    public ScopeVolumeKind GetVolumeKind(string resolvedPath)
    {
        VolumePaths.Add(resolvedPath);
        return volume;
    }

    public IEnumerable<WindowsScopeEntry> EnumerateDirectory(string resolvedPath)
    {
        EnumeratedPaths.Add(resolvedPath);
        if (enumerationFailures.TryGetValue(resolvedPath, out var failure))
        {
            throw failure;
        }

        return entries.TryGetValue(resolvedPath, out var directoryEntries)
            ? directoryEntries
            : [];
    }

    public bool CanOpenForRead(string path)
    {
        ReadPaths.Add(path);
        return !readable.TryGetValue(path, out var canRead) || canRead;
    }

    public bool IsCodecAvailable(SupportedImageFormat format)
    {
        CodecQueries.Add(format);
        return !codecs.TryGetValue(format, out var available) || available;
    }

    private List<WindowsScopeEntry> EntriesFor(string directory)
    {
        if (!entries.TryGetValue(directory, out var directoryEntries))
        {
            directoryEntries = [];
            entries.Add(directory, directoryEntries);
        }

        return directoryEntries;
    }
}
