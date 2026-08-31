using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

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

    public bool TryOpenForRead(string path)
    {
        using var readHandle = NativeMethods.CreateFile(
            WindowsScopeNative.ToExtendedLengthPath(path),
            NativeMethods.GenericRead,
            FileShare.ReadWrite | FileShare.Delete,
            securityAttributes: 0,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            templateFile: 0);
        return !readHandle.IsInvalid;
    }
}
