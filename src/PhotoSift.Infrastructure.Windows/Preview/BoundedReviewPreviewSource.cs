using System.Globalization;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Windows.Preview;

public sealed class BoundedReviewPreviewSource : IReviewPreviewSource
{
    private readonly IProtectedFileReader reader;
    private readonly WicPreviewDecoder decoder;
    private readonly BoundedThumbnailCache cache;
    private readonly int maximumEdge;
    private int disposed;

    public BoundedReviewPreviewSource(
        IProtectedFileReader reader,
        WicPreviewDecoder decoder,
        BoundedThumbnailCache cache,
        int maximumEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEdge);
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.maximumEdge = maximumEdge;
    }

    public async Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(occurrence);
        if (occurrence.PhysicalFileKey is not { } physical || occurrence.Content is not { } content)
            return ReviewPreviewResult.Failure(
                occurrence.ResolvedPath,
                PreviewAvailability.Unavailable,
                "Preview unavailable: source evidence is unavailable.");

        await using var lease = await reader.OpenAsync(new(
            context, occurrence.DiscoveredPath, occurrence.ResolvedPath, physical),
            cancellationToken).ConfigureAwait(false);
        var opened = lease.OpenedFacts;
        if (opened.PhysicalFileKey != content.PhysicalFileKey || opened.ByteLength != content.ByteLength)
            return ReviewPreviewResult.Failure(
                occurrence.ResolvedPath,
                PreviewAvailability.CouldNotAnalyze,
                WicPreviewDecoder.ChangedReason,
                opened.ByteLength,
                opened.LastWriteUtc);

        var key = CacheKey(content);
        if (cache.TryGet(key, out var cached))
            return ReviewPreviewResult.Success(
                occurrence.ResolvedPath, cached!, opened.ByteLength, opened.LastWriteUtc);

        using var decoded = await decoder.DecodeAsync(
            lease, occurrence.ResolvedPath, maximumEdge, cancellationToken).ConfigureAwait(false);
        if (decoded.Frame is null)
            return ReviewPreviewResult.Failure(
                decoded.FullPath,
                decoded.Availability,
                decoded.Reason,
                opened.ByteLength,
                opened.LastWriteUtc);

        var frame = decoded.TakeFrame();
        try
        {
            cache.Add(key, frame.Clone());
            return ReviewPreviewResult.Success(
                decoded.FullPath, frame, opened.ByteLength, opened.LastWriteUtc);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            cache.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string CacheKey(ContentRevision content) => string.Create(
        CultureInfo.InvariantCulture,
        $"{content.PhysicalFileKey.VolumeSerial:X16}:{content.PhysicalFileKey.FileId.High:X16}{content.PhysicalFileKey.FileId.Low:X16}:{content.ByteLength}:{content.Sha256}");
}
