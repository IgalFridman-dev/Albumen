using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Windows.Preview;

public sealed class WicNearFeatureSource(
    IProtectedFileReader reader,
    IExactContentAnalyzer exact,
    WicPreviewDecoder decoder,
    IPerceptualSignatureAnalyzer signatures) : INearFeatureSource
{
    public const int MaximumEdge = 256;

    public async ValueTask<NearFeatureResult> AnalyzeAsync(
        ScanRunContext context, ScanFileOccurrence row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.PhysicalFileKey is not { } physical || row.Content is not { } content)
            return Failure(row, ScanStageState.NotApplicable,
                row.Exact.Reason == ScanReason.None ? ScanReason.CapabilityUnavailable : row.Exact.Reason);

        await using var lease = await reader.OpenAsync(new(
            context,
            row.DiscoveredPath, row.ResolvedPath, physical), cancellationToken).ConfigureAwait(false);
        var digest = await exact.HashAsync(lease.Content, content.ByteLength, cancellationToken).ConfigureAwait(false);
        if (digest.ByteLength != content.ByteLength
            || !string.Equals(digest.Sha256, content.Sha256, StringComparison.OrdinalIgnoreCase))
            return Failure(row, ScanStageState.FailedRetryable, ScanReason.Changed);

        using var decoded = await decoder.DecodeAsync(
            lease, row.ResolvedPath, MaximumEdge, cancellationToken).ConfigureAwait(false);
        if (decoded.Frame is not { } frame)
            return decoded.Availability == PreviewAvailability.Unsupported
                ? Failure(row, ScanStageState.Unsupported, ScanReason.CodecUnavailable)
                : Failure(row, ScanStageState.FailedRetryable, ScanReason.ReadFailure);
        var signature = signatures.Extract(
            frame.Width, frame.Height, frame.AnalysisPixels.Span);
        return new(row.Token, content, signature,
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None));
    }

    private static NearFeatureResult Failure(ScanFileOccurrence row,
        ScanStageState state, ScanReason reason) =>
        new(row.Token, row.Content, null, new StageOutcome(state, reason));
}
