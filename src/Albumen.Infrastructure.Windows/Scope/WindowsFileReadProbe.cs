using Albumen.Domain.Scope;

namespace Albumen.Infrastructure.Windows.Scope;

internal sealed class WindowsFileReadProbe : IWindowsFileReadProbe
{
    public ScopeCloudState GetCloudState(string path)
    {
        using var findHandle = NativeMethods.FindFirstFileEx(
            WindowsScopeNative.ToExtendedLengthPath(path),
            FindExInfoLevel.Basic,
            out var findData,
            FindExSearchOperation.NameMatch,
            searchFilter: 0,
            additionalFlags: 0);
        if (findHandle.IsInvalid)
        {
            return ScopeCloudState.Unknown;
        }

        return WindowsScopeNative.MapCloudState(
            findData.FileAttributes,
            NativeMethods.CfGetPlaceholderStateFromAttributeTag(
                findData.FileAttributes,
                findData.Reserved0));
    }

    public unsafe bool TryOpenForRead(string path)
    {
        using var readHandle = NativeMethods.CreateFile(
            WindowsScopeNative.ToExtendedLengthPath(path),
            NativeMethods.GenericRead,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal |
                NativeMethods.FileFlagOpenNoRecall |
                NativeMethods.FileFlagOpenReparsePoint,
            templateFile: 0);
        if (readHandle.IsInvalid)
        {
            return false;
        }

        if (!NativeMethods.GetFileInformationByHandleEx(
                readHandle,
                FileInfoByHandleClass.FileAttributeTagInfo,
                out FILE_ATTRIBUTE_TAG_INFO information,
                checked((uint)sizeof(FILE_ATTRIBUTE_TAG_INFO))))
        {
            return false;
        }

        return (information.FileAttributes & NativeMethods.FileAttributeReparsePoint) == 0;
    }
}
