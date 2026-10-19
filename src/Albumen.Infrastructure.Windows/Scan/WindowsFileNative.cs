using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scope;

namespace Albumen.Infrastructure.Windows.Scan;

internal interface IWindowsFileFactsSource
{
    WindowsFileObservation Read(SafeFileHandle handle, string path);
}

internal sealed record WindowsFileObservation(
    PhysicalFileKey? PhysicalFileKey,
    long? ByteLength,
    string? ResolvedPath,
    uint? Attributes,
    uint? ReparseTag,
    ScopeCloudState CloudState,
    ScopeVolumeKind VolumeKind,
    bool? DeletePending,
    bool? IsDirectory,
    DateTimeOffset? LastWriteUtc,
    AllocationEvidence Allocation);

internal sealed partial class WindowsFileNative : IWindowsFileFactsSource
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenNoRecall = 0x00100000;
    private const uint SequentialScan = 0x08000000;
    private const uint BackupSemantics = 0x02000000;

    internal static SafeFileHandle OpenContent(string path) =>
        Open(path, GenericRead, OpenReparsePoint | OpenNoRecall | SequentialScan);

    // Metadata-only inspection does not participate in share denial. Content
    // reads revalidate these observations; traversal uses retained guards below.
    internal static SafeFileHandle OpenMetadata(string path, bool followDirectoryLink = false) =>
        Open(path, 0, BackupSemantics | OpenNoRecall | (followDirectoryLink ? 0 : OpenReparsePoint));

    internal static SafeFileHandle OpenDirectoryForEnumeration(string path) =>
        Open(path, 0x00000001 /* FILE_LIST_DIRECTORY */, BackupSemantics | OpenNoRecall | OpenReparsePoint);

    // FILE_TRAVERSE shares the FILE_EXECUTE bit: unlike access=0 metadata
    // requests, this participates in share-access checks and pins the name.
    internal static SafeFileHandle OpenDirectoryGuard(string path, bool followDirectoryLink) =>
        Open(path, 0x00000020 /* FILE_TRAVERSE */, BackupSemantics | OpenNoRecall | (followDirectoryLink ? 0 : OpenReparsePoint));

    private static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var handle = CreateFile(WindowsScopeNative.ToExtendedLengthPath(path), access, ShareRead,
            0, OpenExisting, flags, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw Failure(error, path);
    }

    public unsafe WindowsFileObservation Read(SafeFileHandle handle, string path)
    {
        if (!NativeMethods.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileIdInfo,
            out FILE_ID_INFO identity, (uint)sizeof(FILE_ID_INFO))
            || !GetStandardInformation(handle, 1, out var standard, (uint)sizeof(FileStandardInformation))
            || !NativeMethods.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileAttributeTagInfo,
                out FILE_ATTRIBUTE_TAG_INFO attributes, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            throw Unknown(path, Marshal.GetLastPInvokeError());
        }

        var finalPath = FinalPath(handle, path);
        var volume = WindowsScopeNative.MapDriveType(NativeMethods.GetDriveType(Path.GetPathRoot(finalPath)!));
        ScopeCloudState cloud;
        try
        {
            cloud = WindowsScopeNative.MapCloudState(attributes.FileAttributes,
                NativeMethods.CfGetPlaceholderStateFromAttributeTag(attributes.FileAttributes, attributes.ReparseTag));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new ScanReadException(new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
                "Cloud safety facts are unavailable.", exception);
        }

        DateTimeOffset? lastWrite = null;
        if (GetBasicInformation(handle, 0, out var basic, (uint)sizeof(FileBasicInformation))
            && basic.LastWriteTime >= 0 && basic.LastWriteTime <= 2650467743999999999L)
        {
            lastWrite = DateTimeOffset.FromFileTime(basic.LastWriteTime).ToUniversalTime();
        }

        long? allocation = standard.AllocationSize >= 0 ? standard.AllocationSize : null;
        uint? links = standard.NumberOfLinks > 0 ? standard.NumberOfLinks : null;
        return new WindowsFileObservation(
            new PhysicalFileKey(identity.VolumeSerialNumber, new FileId128(identity.FileId.High, identity.FileId.Low)),
            standard.EndOfFile, finalPath, attributes.FileAttributes, attributes.ReparseTag,
            cloud, volume, standard.DeletePending != 0, standard.Directory != 0, lastWrite,
            new AllocationEvidence(allocation, links, allocation.HasValue && links.HasValue));
    }

    private static string FinalPath(SafeFileHandle handle, string path)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                throw Unknown(path, Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Length)
            {
                return WindowsFileFactsReader.NormalizePath(new string(buffer, 0, (int)length));
            }

            buffer = new char[checked((int)length + 1)];
        }
    }

    internal static ScanReadException Failure(int error, string path)
    {
        var reason = error switch
        {
            2 or 3 or 15 or 21 => ScanReason.Missing,
            5 => ScanReason.AccessDenied,
            32 or 33 => ScanReason.SharingViolation,
            _ => ScanReason.ReadFailure,
        };
        return new ScanReadException(new StageOutcome(ScanStageState.FailedRetryable, reason),
            $"The protected file could not be opened: {path}", new Win32Exception(error));
    }

    private static ScanReadException Unknown(string path, int error) =>
        new(new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
            $"Required handle facts could not be established: {path}", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetStandardInformation(SafeFileHandle handle, int informationClass,
        out FileStandardInformation information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetBasicInformation(SafeFileHandle handle, int informationClass,
        out FileBasicInformation information, uint bufferSize);
}
