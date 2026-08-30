using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal static partial class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const FileMode OpenExisting = FileMode.Open;
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileAttributeOffline = 0x00001000;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint FileAttributeRecallOnOpen = 0x00040000;
    internal const uint FileAttributeRecallOnDataAccess = 0x00400000;
    internal const uint FileFlagBackupSemantics = 0x02000000;

    internal const uint DriveUnknown = 0;
    internal const uint DriveNoRootDirectory = 1;
    internal const uint DriveRemovable = 2;
    internal const uint DriveFixed = 3;
    internal const uint DriveRemote = 4;
    internal const uint DriveCdRom = 5;
    internal const uint DriveRamDisk = 6;

    internal const uint CfPlaceholderStatePlaceholder = 0x00000001;
    internal const uint CfPlaceholderStateSyncRoot = 0x00000002;
    internal const uint CfPlaceholderStateInvalid = uint.MaxValue;

    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidDrive = 15;
    internal const int ErrorNotReady = 21;
    internal const int ErrorSharingViolation = 32;
    internal const int ErrorLockViolation = 33;
    internal const int ErrorBadNetPath = 53;
    internal const int ErrorBadNetName = 67;
    internal const int ErrorNoMoreFiles = 18;

    internal const int Success = 0;
    internal const int SuccessAlreadyInitialized = 1;
    internal const int RpcChangedMode = unchecked((int)0x80010106);
    internal const uint ClassContextInProcessServer = 0x1;
    internal const uint CoInitializeMultithreaded = 0x0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char[] path,
        uint pathLength,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FILE_ID_INFO information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        out FILE_ATTRIBUTE_TAG_INFO information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFindHandle FindFirstFileEx(
        string fileName,
        FindExInfoLevel infoLevel,
        out WIN32_FIND_DATAW findFileData,
        FindExSearchOperation searchOperation,
        nint searchFilter,
        uint additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindNextFile(
        SafeFindHandle findFile,
        out WIN32_FIND_DATAW findFileData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindClose(nint findFile);

    [LibraryImport("cldapi.dll")]
    internal static partial uint CfGetPlaceholderStateFromAttributeTag(
        uint fileAttributes,
        uint reparseTag);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint classContext,
        in Guid interfaceId,
        out nint instance);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
}
