using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;

namespace Albumen.Infrastructure.Windows.Preview;

/// <summary>
/// The encoder's decode (E4-S1: WIC at a 512 edge, EXIF-oriented BGRA), taken only after the file
/// is proven to hold the content the Scan hashed.
/// </summary>
public sealed class WicEncoderImageSource(
    IProtectedFileReader reader,
    IExactContentAnalyzer exact,
    WicPreviewDecoder decoder) : IEncoderImageSource
{
    public async ValueTask<EncoderImage?> ReadAsync(
        ScanRunContext context, ScanFileOccurrence row, int maximumEdge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.PhysicalFileKey is not { } physical || row.Content is not { } content) return null;

        await using var lease = await reader.OpenAsync(new(
            context, row.DiscoveredPath, row.ResolvedPath, physical), cancellationToken).ConfigureAwait(false);
        var digest = await exact.HashAsync(lease.Content, content.ByteLength, cancellationToken).ConfigureAwait(false);
        if (digest.ByteLength != content.ByteLength
            || !string.Equals(digest.Sha256, content.Sha256, StringComparison.OrdinalIgnoreCase))
            return null;

        using var decoded = await decoder.DecodeAsync(
            lease, row.ResolvedPath, maximumEdge, cancellationToken).ConfigureAwait(false);
        return decoded.Frame is { } frame
            ? new EncoderImage(frame.AnalysisPixels.ToArray(), frame.Width, frame.Height)
            : null;
    }
}
