using System.Runtime.InteropServices;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scan;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal sealed class WindowsScopeNative : IWindowsScopeNative
{
    private readonly WicCodecCapability codecs;
    private readonly IWindowsFileReadProbe fileReadProbe;
    private readonly IWindowsFileFactsSource fileFacts;

    internal WindowsScopeNative(WicCodecCapability codecs)
        : this(codecs, new WindowsFileReadProbe())
    {
    }

    internal WindowsScopeNative(
        WicCodecCapability codecs,
        IWindowsFileReadProbe fileReadProbe)
        : this(codecs, fileReadProbe, new WindowsFileNative())
    {
    }

    internal WindowsScopeNative(WicCodecCapability codecs, IWindowsFileReadProbe fileReadProbe,
        IWindowsFileFactsSource fileFacts)
    {
        this.codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));
        this.fileReadProbe = fileReadProbe ??
            throw new ArgumentNullException(nameof(fileReadProbe));
        this.fileFacts = fileFacts ?? throw new ArgumentNullException(nameof(fileFacts));
    }

    public WindowsDirectoryFacts InspectDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var displayPath = Path.GetFullPath(path);

        using var handle = NativeMethods.CreateFile(
            ToExtendedLengthPath(displayPath),
            desiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenNoRecall,
            templateFile: 0);
        ThrowIfInvalid(handle.IsInvalid, displayPath);

        var resolvedPath = GetFinalPath(handle, displayPath);
        var identity = GetIdentity(handle, displayPath);
        var attributes = GetAttributeTag(handle, displayPath);
        var placeholderState = NativeMethods.CfGetPlaceholderStateFromAttributeTag(
            attributes.FileAttributes,
            attributes.ReparseTag);

        return new WindowsDirectoryFacts(
            displayPath,
            resolvedPath,
            identity,
            (attributes.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0,
            MapCloudState(attributes.FileAttributes, placeholderState));
    }

    public ScopeVolumeKind GetVolumeKind(string resolvedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        var root = NormalizeDriveRoot(resolvedPath);
        return string.IsNullOrEmpty(root)
            ? ScopeVolumeKind.Unknown
            : MapDriveType(NativeMethods.GetDriveType(root));
    }

    public IEnumerable<WindowsScopeEntry> EnumerateDirectory(string resolvedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        var root = InspectDirectory(resolvedPath);
        var scope = new ResolvedScope(new ScopeSelection(resolvedPath, ScopeRecursion.TopLevelOnly), root.ResolvedPath, root.Identity);
        using var protection = ProtectDirectory(scope, root, resolvedPath, CancellationToken.None);
        foreach (var entry in protection.Enumerate(CancellationToken.None)) yield return entry;
    }

    public IWindowsDirectoryProtection ProtectDirectory(ResolvedScope scope, WindowsDirectoryFacts expected,
        string discoveredPath, CancellationToken cancellationToken) =>
        new WindowsDirectoryProtection(scope, expected, discoveredPath, fileFacts, cancellationToken);

    public WindowsFileReadProbeResult ProbeFileForRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var cloudState = fileReadProbe.GetCloudState(path);
        if (cloudState is not ScopeCloudState.Local)
        {
            return new WindowsFileReadProbeResult(
                cloudState,
                CanOpenForRead: false);
        }

        return new WindowsFileReadProbeResult(
            cloudState,
            fileReadProbe.TryOpenForRead(path));
    }

    public PhysicalFileKey InspectFileIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var displayPath = Path.GetFullPath(path);
        using var handle = NativeMethods.CreateFile(
            ToExtendedLengthPath(displayPath),
            desiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOpenNoRecall | NativeMethods.FileFlagOpenReparsePoint,
            templateFile: 0);
        ThrowIfInvalid(handle.IsInvalid, displayPath);

        var information = GetIdentityInformation(handle, displayPath);
        return new PhysicalFileKey(
            information.VolumeSerialNumber,
            new FileId128(information.FileId.High, information.FileId.Low));
    }

    public bool IsCodecAvailable(SupportedImageFormat format) => codecs.IsAvailable(format);

    internal static ScopeVolumeKind MapDriveType(uint driveType) => driveType switch
    {
        NativeMethods.DriveFixed => ScopeVolumeKind.Fixed,
        NativeMethods.DriveRemovable => ScopeVolumeKind.Removable,
        NativeMethods.DriveRemote => ScopeVolumeKind.Remote,
        NativeMethods.DriveNoRootDirectory => ScopeVolumeKind.Unavailable,
        NativeMethods.DriveUnknown => ScopeVolumeKind.Unknown,
        NativeMethods.DriveCdRom or NativeMethods.DriveRamDisk => ScopeVolumeKind.Unsupported,
        _ => ScopeVolumeKind.Unknown,
    };

    internal static string? NormalizeDriveRoot(string resolvedPath)
    {
        var root = Path.GetPathRoot(resolvedPath);
        return string.IsNullOrEmpty(root) || Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
    }

    internal static string ToExtendedLengthPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";

        if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            ? extendedUncPrefix + path[2..]
            : extendedPrefix + path;
    }

    internal static ScopeCloudState MapCloudState(uint attributes, uint placeholderState)
    {
        const uint recall = NativeMethods.FileAttributeRecallOnOpen |
            NativeMethods.FileAttributeRecallOnDataAccess |
            NativeMethods.FileAttributeOffline;
        if (placeholderState == NativeMethods.CfPlaceholderStateInvalid)
        {
            return ScopeCloudState.Unknown;
        }

        if ((placeholderState & NativeMethods.CfPlaceholderStateSyncRoot) != 0)
        {
            return ScopeCloudState.Synchronized;
        }

        return (placeholderState & NativeMethods.CfPlaceholderStatePlaceholder) != 0 ||
            (attributes & recall) != 0
            ? ScopeCloudState.Placeholder
            : ScopeCloudState.Local;
    }

    private static string GetFinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Length),
                flags: 0);
            if (length == 0)
            {
                throw CreateException(path, Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Length)
            {
                return NormalizeFinalPath(new string(buffer, 0, checked((int)length)));
            }

            buffer = new char[checked((int)length + 1)];
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";

        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static unsafe DirectoryIdentity GetIdentity(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string path)
    {
        var information = GetIdentityInformation(handle, path);
        return new DirectoryIdentity(
            information.VolumeSerialNumber,
            information.FileId.High,
            information.FileId.Low);
    }

    private static unsafe FILE_ID_INFO GetIdentityInformation(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string path)
    {
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileIdInfo,
                out FILE_ID_INFO information,
                checked((uint)sizeof(FILE_ID_INFO))))
        {
            throw CreateException(path, Marshal.GetLastPInvokeError());
        }

        return information;
    }

    private static unsafe FILE_ATTRIBUTE_TAG_INFO GetAttributeTag(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string path)
    {
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FILE_ATTRIBUTE_TAG_INFO information,
                checked((uint)sizeof(FILE_ATTRIBUTE_TAG_INFO))))
        {
            throw CreateException(path, Marshal.GetLastPInvokeError());
        }

        return information;
    }

    private static void ThrowIfInvalid(bool isInvalid, string path)
    {
        if (isInvalid)
        {
            throw CreateException(path, Marshal.GetLastPInvokeError());
        }
    }

    private static WindowsScopeNativeException CreateException(string path, int nativeError) =>
        new(MapFailure(nativeError), path, nativeError);

    private static WindowsScopeFailureKind MapFailure(int nativeError) => nativeError switch
    {
        NativeMethods.ErrorAccessDenied or
        NativeMethods.ErrorSharingViolation or
        NativeMethods.ErrorLockViolation => WindowsScopeFailureKind.AccessDenied,
        NativeMethods.ErrorFileNotFound or
        NativeMethods.ErrorPathNotFound or
        NativeMethods.ErrorInvalidDrive or
        NativeMethods.ErrorNotReady or
        NativeMethods.ErrorBadNetPath or
        NativeMethods.ErrorBadNetName => WindowsScopeFailureKind.Unavailable,
        _ => WindowsScopeFailureKind.Unknown,
    };
}
