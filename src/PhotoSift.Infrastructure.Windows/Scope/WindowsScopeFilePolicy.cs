using System.ComponentModel;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

internal sealed class WindowsScopeFilePolicy
{
    private readonly Dictionary<SupportedImageFormat, bool> codecAvailability = [];
    private readonly IWindowsScopeNative native;

    internal WindowsScopeFilePolicy(IWindowsScopeNative native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    internal WindowsScopeFileResult Inspect(
        string discoveredPath,
        WindowsScopeEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discoveredPath);
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.CloudState is not ScopeCloudState.Local)
        {
            return Blocked(discoveredPath, entry.Path, entry.CloudState);
        }

        var format = Recognize(entry.Path);
        if (format is null)
        {
            return File(
                discoveredPath,
                entry.Path,
                physicalFileKey: null,
                ScanStageState.Unsupported,
                ScanReason.UnsupportedExtension);
        }

        if (entry.IsReparsePoint)
        {
            return File(
                discoveredPath,
                entry.Path,
                physicalFileKey: null,
                ScanStageState.FailedTerminal,
                ScanReason.UnsafeLocation);
        }

        if (!CodecIsAvailable(format.Value))
        {
            return File(
                discoveredPath,
                entry.Path,
                physicalFileKey: null,
                ScanStageState.Unsupported,
                ScanReason.CodecUnavailable);
        }

        WindowsFileReadProbeResult read;
        try
        {
            read = native.ProbeFileForRead(entry.Path);
        }
        catch (Exception exception) when (TryMapFileFailure(exception, out var outcome))
        {
            return File(discoveredPath, entry.Path, physicalFileKey: null, outcome);
        }

        if (read.CloudState is not ScopeCloudState.Local)
        {
            return Blocked(discoveredPath, entry.Path, read.CloudState);
        }

        if (!read.CanOpenForRead)
        {
            return File(
                discoveredPath,
                entry.Path,
                physicalFileKey: null,
                ScanStageState.FailedRetryable,
                ScanReason.AccessDenied);
        }

        try
        {
            return File(
                discoveredPath,
                entry.Path,
                native.InspectFileIdentity(entry.Path),
                ScanStageState.Succeeded,
                ScanReason.None);
        }
        catch (Exception exception) when (TryMapFileFailure(exception, out var outcome))
        {
            return File(discoveredPath, entry.Path, physicalFileKey: null, outcome);
        }
    }

    internal static SupportedImageFormat? Recognize(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JPG" or ".JPEG" => SupportedImageFormat.Jpeg,
            ".PNG" => SupportedImageFormat.Png,
            ".HEIC" => SupportedImageFormat.Heic,
            _ => null,
        };

    private bool CodecIsAvailable(SupportedImageFormat format)
    {
        if (codecAvailability.TryGetValue(format, out var available))
        {
            return available;
        }

        available = native.IsCodecAvailable(format);
        codecAvailability.Add(format, available);
        return available;
    }

    private static bool TryMapFileFailure(Exception exception, out StageOutcome outcome)
    {
        outcome = exception switch
        {
            UnauthorizedAccessException =>
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.AccessDenied } =>
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.Unavailable } =>
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Missing),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.Unknown } or
            IOException or Win32Exception =>
                new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
            _ => null!,
        };
        return outcome is not null;
    }

    private static WindowsScopeFileResult File(
        string discoveredPath,
        string resolvedPath,
        PhysicalFileKey? physicalFileKey,
        ScanStageState state,
        ScanReason reason) =>
        File(
            discoveredPath,
            resolvedPath,
            physicalFileKey,
            new StageOutcome(state, reason));

    private static WindowsScopeFileResult File(
        string discoveredPath,
        string resolvedPath,
        PhysicalFileKey? physicalFileKey,
        StageOutcome outcome) =>
        new(
            new EnumeratedFile(
                discoveredPath,
                resolvedPath,
                physicalFileKey,
                outcome),
            BlockingCloud: null,
            BlocksForUnknownFailure:
                outcome is { State: ScanStageState.FailedTerminal, Reason: ScanReason.UnknownSafety });

    private static WindowsScopeFileResult Blocked(
        string discoveredPath,
        string resolvedPath,
        ScopeCloudState cloud) =>
        new(
            new EnumeratedFile(
                discoveredPath,
                resolvedPath,
                PhysicalFileKey: null,
                cloud is ScopeCloudState.Unknown
                    ? new StageOutcome(
                        ScanStageState.FailedTerminal,
                        ScanReason.UnknownSafety)
                    : new StageOutcome(
                        ScanStageState.FailedTerminal,
                        ScanReason.UnsafeLocation)),
            cloud,
            BlocksForUnknownFailure: false);
}

internal sealed record WindowsScopeFileResult(
    EnumeratedFile File,
    ScopeCloudState? BlockingCloud,
    bool BlocksForUnknownFailure);
