using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.Infrastructure.Windows.Action;

// Action adds typed mutation-safety checks to the shared handle observations.
// No Scan lease or asynchronous callback operation is used.
internal sealed class WindowsActionFileNative(IWindowsFileFactsSource source) : IWindowsFileFactsSource
{
    // Action-only handoff: preserve content write denial while allowing the
    // Shell's preparation access. Shared Scan/directory guard semantics do not change.
    internal static SafeFileHandle OpenSelectedHandoff(string path)
    {
        var original = WindowsFileFactsReader.NormalizePath(path);
        var handle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(original), 0x80000000,
            FileShare.Read | FileShare.Delete, 0, FileMode.Open, 0x08300000, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw WindowsFileNative.Failure(error, original);
    }

    public WindowsFileObservation Read(SafeFileHandle handle, string path)
    {
        var observation = source.Read(handle, path);
        if (observation.CloudState == ScopeCloudState.Synchronized) throw new WindowsActionLeaseException(ActionSafetyReason.Synchronized);
        if (observation.CloudState == ScopeCloudState.Placeholder) throw new WindowsActionLeaseException(ActionSafetyReason.Placeholder);
        if (observation.IsDirectory is false)
        {
            if ((observation.Attributes & 1) != 0) throw new WindowsActionLeaseException(ActionSafetyReason.ReadOnly);
            if (observation.LastWriteUtc is null) throw new WindowsActionLeaseException(ActionSafetyReason.UnknownSafety);
        }
        return observation;
    }

    internal static ActionSafetyReason Reason(Exception exception) => exception switch
    {
        WindowsActionLeaseException action => action.Reason,
        OperationCanceledException => ActionSafetyReason.Interrupted,
        ScanReadException read => read.Outcome.Reason switch
        {
            Domain.Scan.ScanReason.Missing => ActionSafetyReason.Missing,
            Domain.Scan.ScanReason.Changed => ActionSafetyReason.Changed,
            Domain.Scan.ScanReason.AccessDenied => ActionSafetyReason.AccessDenied,
            Domain.Scan.ScanReason.SharingViolation => ActionSafetyReason.SharingViolation,
            Domain.Scan.ScanReason.UnsafeLocation => ActionSafetyReason.UnsafeLocation,
            Domain.Scan.ScanReason.UnknownSafety => ActionSafetyReason.UnknownSafety,
            _ => ActionSafetyReason.Unavailable,
        },
        UnauthorizedAccessException => ActionSafetyReason.AccessDenied,
        IOException => ActionSafetyReason.Unavailable,
        _ => ActionSafetyReason.UnknownSafety,
    };
}

internal sealed class WindowsActionLeaseException(ActionSafetyReason reason, Exception? innerException = null)
    : Exception($"Action protection failed: {reason}.", innerException)
{
    internal ActionSafetyReason Reason { get; } = reason;
}
