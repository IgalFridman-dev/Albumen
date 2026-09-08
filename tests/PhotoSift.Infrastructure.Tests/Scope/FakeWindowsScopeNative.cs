using PhotoSift.Domain.Scan;
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
    private readonly Dictionary<string, WindowsFileReadProbeResult> readResults =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> readProbeFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhysicalFileKey> fileIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> fileIdentityFailures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SupportedImageFormat, bool> codecs = [];
    private readonly ScopeVolumeKind volume;
    private ulong nextFileIdentity = 100;

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

    public List<string> ReadProbePaths { get; } = [];

    public List<string> ReadOpenPaths { get; } = [];

    public List<string> InspectedFileIdentityPaths { get; } = [];

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
        bool canRead = true,
        ScopeCloudState? readCloud = null,
        bool isReparsePoint = false,
        PhysicalFileKey? physicalFileKey = null)
    {
        EntriesFor(directory).Add(new WindowsScopeEntry(
            path,
            IsDirectory: false,
            isReparsePoint,
            cloud));
        readResults[path] = new WindowsFileReadProbeResult(
            readCloud ?? cloud,
            canRead);
        fileIdentities[path] = physicalFileKey ?? FileIdentity(nextFileIdentity++);
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

    public FakeWindowsScopeNative FailFileIdentity(string path, Exception exception)
    {
        fileIdentityFailures[path] = exception;
        return this;
    }

    public FakeWindowsScopeNative FailReadProbe(string path, Exception exception)
    {
        readProbeFailures[path] = exception;
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

    public WindowsFileReadProbeResult ProbeFileForRead(string path)
    {
        ReadProbePaths.Add(path);
        if (readProbeFailures.TryGetValue(path, out var failure))
        {
            throw failure;
        }

        var result = readResults.TryGetValue(path, out var configured)
            ? configured
            : new WindowsFileReadProbeResult(ScopeCloudState.Local, CanOpenForRead: true);
        if (result.CloudState is ScopeCloudState.Local)
        {
            ReadOpenPaths.Add(path);
        }

        return result;
    }

    public PhysicalFileKey InspectFileIdentity(string path)
    {
        InspectedFileIdentityPaths.Add(path);
        if (fileIdentityFailures.TryGetValue(path, out var failure))
        {
            throw failure;
        }

        return fileIdentities.TryGetValue(path, out var identity)
            ? identity
            : throw new InvalidOperationException($"No file identity for '{path}'.");
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

    private static PhysicalFileKey FileIdentity(ulong value) =>
        new(7, new FileId128(0, value));
}
