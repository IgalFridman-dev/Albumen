using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Albumen.Application.Action;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;

namespace Albumen.Infrastructure.Windows.Action;

public sealed class WindowsActionRevalidationProbe : IActionRevalidationProbe
{
    private const uint ReparsePoint = 0x400;
    private const uint DirectoryAttribute = 0x10;
    private readonly IWindowsActionRevalidationNative native;

    public WindowsActionRevalidationProbe()
        : this(new WindowsActionRevalidationNative())
    {
    }

    internal WindowsActionRevalidationProbe(IWindowsActionRevalidationNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        this.native = native;
    }

    public async ValueTask<ActionRevalidationProbeResult> ProbeAsync(
        ActionRevalidationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string path;
        try
        {
            path = WindowsFileFactsReader.NormalizePath(request.ResolvedPath);
        }
        catch (Exception exception) when (exception is ScanReadException or ArgumentException or NotSupportedException)
        {
            return Unknown(ActionRevalidationReason.LocationUnavailable, pathExists: false);
        }

        SafeFileHandle? handle = null;
        FileStream? content = null;
        try
        {
            handle = native.OpenReadOnly(path);
            var opened = native.Read(handle, path);
            if (!IsSupportedFile(opened, path))
            {
                return Unknown(ActionRevalidationReason.LocationUnavailable, pathExists: true,
                    opened.PhysicalFileKey, opened.ByteLength);
            }

            if (opened.PhysicalFileKey != request.ReviewedKey || opened.ByteLength != request.ReviewedLength)
            {
                return Unknown(ActionRevalidationReason.OriginalPathChanged, pathExists: true,
                    opened.PhysicalFileKey, opened.ByteLength);
            }

            cancellationToken.ThrowIfCancellationRequested();
            content = new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
            handle = null;
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken)
                .ConfigureAwait(false));
            var final = native.Read(content.SafeFileHandle, path);
            if (!IsSupportedFile(final, path))
            {
                return Unknown(ActionRevalidationReason.LocationUnavailable, pathExists: true,
                    final.PhysicalFileKey, final.ByteLength, sha256);
            }

            if (final.PhysicalFileKey != opened.PhysicalFileKey
                || final.ByteLength != opened.ByteLength
                || !StringComparer.OrdinalIgnoreCase.Equals(sha256, request.ReviewedSha256))
            {
                return Unknown(ActionRevalidationReason.OriginalPathChanged, pathExists: true,
                    final.PhysicalFileKey, final.ByteLength, sha256);
            }

            return new ActionRevalidationProbeResult(ActionRevalidationKind.ConfirmedPresent,
                ActionRevalidationReason.OriginalFileConfirmed,
                new ActionRevalidationObservation(true, final.PhysicalFileKey, final.ByteLength, sha256));
        }
        catch (OperationCanceledException)
        {
            return Unknown(ActionRevalidationReason.TimedOut, pathExists: handle is not null || content is not null);
        }
        catch (ScanReadException exception)
        {
            return exception.Outcome.Reason switch
            {
                ScanReason.Missing => Unknown(ActionRevalidationReason.OriginalPathMissing, pathExists: false),
                ScanReason.AccessDenied or ScanReason.SharingViolation =>
                    Unknown(ActionRevalidationReason.AccessUnavailable, pathExists: true),
                ScanReason.UnsafeLocation => Unknown(ActionRevalidationReason.LocationUnavailable, pathExists: true),
                ScanReason.Changed => Unknown(ActionRevalidationReason.OriginalPathChanged, pathExists: true),
                _ => Unknown(ActionRevalidationReason.Failed, pathExists: true),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Unknown(ActionRevalidationReason.AccessUnavailable, pathExists: true);
        }
        catch (IOException)
        {
            return Unknown(ActionRevalidationReason.Failed, pathExists: true);
        }
        catch (Exception)
        {
            return Unknown(ActionRevalidationReason.Failed, pathExists: true);
        }
        finally
        {
            if (content is not null)
            {
                await content.DisposeAsync().ConfigureAwait(false);
            }
            handle?.Dispose();
        }
    }

    private static bool IsSupportedFile(WindowsFileObservation facts, string expectedPath) =>
        facts.PhysicalFileKey.HasValue
        && facts.ByteLength is >= 0
        && facts.Attributes.HasValue
        && facts.ReparseTag.HasValue
        && facts.DeletePending is false
        && facts.IsDirectory is false
        && facts.CloudState == ScopeCloudState.Local
        && facts.VolumeKind is ScopeVolumeKind.Fixed or ScopeVolumeKind.Removable
        && (facts.Attributes.Value & (ReparsePoint | DirectoryAttribute)) == 0
        && !string.IsNullOrWhiteSpace(facts.ResolvedPath)
        && StringComparer.OrdinalIgnoreCase.Equals(
            WindowsFileFactsReader.NormalizePath(facts.ResolvedPath), expectedPath);

    private static ActionRevalidationProbeResult Unknown(ActionRevalidationReason reason, bool pathExists,
        PhysicalFileKey? key = null, long? length = null, string? sha256 = null) =>
        new(ActionRevalidationKind.StillUnknown, reason,
            new ActionRevalidationObservation(pathExists, key, length, sha256));
}

internal interface IWindowsActionRevalidationNative
{
    SafeFileHandle OpenReadOnly(string path);
    WindowsFileObservation Read(SafeFileHandle handle, string path);
}

internal sealed class WindowsActionRevalidationNative : IWindowsActionRevalidationNative
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenNoRecall = 0x00100000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint SequentialScan = 0x08000000;
    private readonly WindowsFileNative facts = new();

    public SafeFileHandle OpenReadOnly(string path)
    {
        var handle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(path), GenericRead,
            FileShare.Read, 0, FileMode.Open, OpenNoRecall | OpenReparsePoint | SequentialScan, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw WindowsFileNative.Failure(error, path);
    }

    public WindowsFileObservation Read(SafeFileHandle handle, string path) => facts.Read(handle, path);
}
