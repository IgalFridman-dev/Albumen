using System.Reflection;
using System.Runtime.InteropServices;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Boundary;

public sealed class WindowsInteropBoundaryTests
{
    [Fact]
    public void NativeMethodsDeclaresOnlyTheDocumentedWindowsInteropSurface()
    {
        var imports = typeof(NativeMethods)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => new
            {
                Method = method.Name,
                Import = method.GetCustomAttribute<LibraryImportAttribute>(),
            })
            .Where(entry => entry.Import is not null)
            .Select(entry => $"{entry.Import!.LibraryName}:{entry.Method}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "cldapi.dll:CfGetPlaceholderStateFromAttributeTag",
            "kernel32.dll:CreateFile",
            "kernel32.dll:FindClose",
            "kernel32.dll:FindFirstFileEx",
            "kernel32.dll:FindNextFile",
            "kernel32.dll:GetDriveType",
            "kernel32.dll:GetFileInformationByHandleEx",
            "kernel32.dll:GetFileInformationByHandleEx",
            "kernel32.dll:GetFinalPathNameByHandle",
            "ole32.dll:CoCreateInstance",
            "ole32.dll:CoInitializeEx",
            "ole32.dll:CoUninitialize",
        ],
        imports);
    }
}
