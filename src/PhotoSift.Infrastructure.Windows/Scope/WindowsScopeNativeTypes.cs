using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal sealed record WindowsDirectoryFacts(
    string DisplayPath,
    string ResolvedPath,
    DirectoryIdentity Identity,
    bool IsReparsePoint,
    ScopeCloudState CloudState);

internal sealed record WindowsScopeEntry(
    string Path,
    bool IsDirectory,
    bool IsReparsePoint,
    ScopeCloudState CloudState);

internal sealed record WindowsFileReadProbeResult(
    ScopeCloudState CloudState,
    bool CanOpenForRead);

internal sealed class WindowsScopeNativeException(
    WindowsScopeFailureKind kind,
    string path,
    int nativeError) : IOException
{
    public WindowsScopeFailureKind Kind { get; } = kind;

    public string PathValue { get; } = path;

    public int NativeError { get; } = nativeError;
}

internal enum WindowsScopeFailureKind
{
    AccessDenied,
    Unavailable,
    Unknown,
}

internal enum FileInfoByHandleClass
{
    FileAttributeTagInfo = 9,
    FileIdInfo = 18,
}

internal enum FindExInfoLevel
{
    Standard,
    Basic,
}

internal enum FindExSearchOperation
{
    NameMatch,
    LimitToDirectories,
    LimitToDevices,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFileTime
{
    internal uint LowDateTime;
    internal uint HighDateTime;
}

[InlineArray(260)]
internal struct NativeFileNameBuffer
{
    private char element0;
}

[InlineArray(14)]
internal struct NativeAlternateFileNameBuffer
{
    private char element0;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WIN32_FIND_DATAW
{
    internal uint FileAttributes;
    internal NativeFileTime CreationTime;
    internal NativeFileTime LastAccessTime;
    internal NativeFileTime LastWriteTime;
    internal uint FileSizeHigh;
    internal uint FileSizeLow;
    internal uint Reserved0;
    internal uint Reserved1;
    internal NativeFileNameBuffer FileName;
    internal NativeAlternateFileNameBuffer AlternateFileName;

    internal string GetFileName()
    {
        ReadOnlySpan<char> buffer = FileName;
        var terminator = buffer.IndexOf('\0');
        return new string(terminator < 0 ? buffer : buffer[..terminator]);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_128
{
    internal ulong Low;
    internal ulong High;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ID_INFO
{
    internal ulong VolumeSerialNumber;
    internal FILE_ID_128 FileId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FILE_ATTRIBUTE_TAG_INFO
{
    internal uint FileAttributes;
    internal uint ReparseTag;
}

internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}
