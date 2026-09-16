using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Windows.Preview;

public sealed class BoundedReviewPreviewSource : IReviewPreviewSource
{
    private readonly IProtectedFileReader reader;
    private readonly WicPreviewDecoder decoder;
    private readonly BoundedThumbnailCache cache;
    private readonly int maximumEdge;
    private readonly SemaphoreSlim lifetimeGate = new(1, 1);
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
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(occurrence);
        await lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (occurrence.PhysicalFileKey is not { } physical || occurrence.Content is not { } content)
                return ReviewPreviewResult.Failure(
                    occurrence.ResolvedPath,
                    PreviewAvailability.Unavailable,
                    "Preview unavailable: source evidence is unavailable.");

            try
            {
                await using var lease = await reader.OpenAsync(new(
                    context, occurrence.DiscoveredPath, occurrence.ResolvedPath, physical),
                    cancellationToken).ConfigureAwait(false);
                var opened = lease.OpenedFacts;
                if (opened.PhysicalFileKey != content.PhysicalFileKey
                    || opened.ByteLength != content.ByteLength
                    || !await MatchesPinnedContentAsync(
                        lease.Content, content, cancellationToken).ConfigureAwait(false))
                    return Changed(occurrence.ResolvedPath, opened);

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
                if (!await MatchesPinnedContentAsync(
                    lease.Content, content, cancellationToken).ConfigureAwait(false))
                    return Changed(occurrence.ResolvedPath, opened);

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
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (TryMapOpenFailure(
                occurrence.ResolvedPath, exception, out var failure))
            {
                return failure!;
            }
        }
        finally
        {
            lifetimeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            cache.Dispose();
        }
        finally
        {
            lifetimeGate.Release();
        }
    }

    private static Task<bool> MatchesPinnedContentAsync(
        Stream stream,
        ContentRevision expected,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                length += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            stream.Position = 0;
        }
        return length == expected.ByteLength
            && Convert.ToHexString(hash.GetHashAndReset()).Equals(
                expected.Sha256, StringComparison.OrdinalIgnoreCase);
    }, cancellationToken);

    private static ReviewPreviewResult Changed(string path, ProtectedFileFacts opened) =>
        ReviewPreviewResult.Failure(path, PreviewAvailability.CouldNotAnalyze,
            WicPreviewDecoder.ChangedReason, opened.ByteLength, opened.LastWriteUtc);

    private static bool TryMapOpenFailure(
        string path,
        Exception exception,
        out ReviewPreviewResult? failure)
    {
        (PreviewAvailability Availability, string Reason)? outcome = exception switch
        {
            ScanReadException { Outcome.Reason: ScanReason.Changed } =>
                (PreviewAvailability.CouldNotAnalyze, WicPreviewDecoder.ChangedReason),
            ScanReadException { Outcome.Reason: ScanReason.AccessDenied } or UnauthorizedAccessException =>
                (PreviewAvailability.Unavailable, "Preview unavailable: access to the file was denied."),
            ScanReadException { Outcome.Reason: ScanReason.UnsafeLocation or ScanReason.UnknownSafety } =>
                (PreviewAvailability.Unavailable, "Preview unavailable: the file is outside the approved Scope."),
            ScanReadException { Outcome.Reason: ScanReason.Missing } or
                FileNotFoundException or DirectoryNotFoundException =>
                (PreviewAvailability.Unavailable, WicPreviewDecoder.FileUnavailableReason),
            ScanReadException or IOException =>
                (PreviewAvailability.Unavailable, "Preview unavailable: the file could not be read."),
            _ => null,
        };
        failure = outcome is { } mapped
            ? ReviewPreviewResult.Failure(path, mapped.Availability, mapped.Reason)
            : null;
        return failure is not null;
    }

    private static string CacheKey(ContentRevision content) => string.Create(
        CultureInfo.InvariantCulture,
        $"{content.PhysicalFileKey.VolumeSerial:X16}:{content.PhysicalFileKey.FileId.High:X16}{content.PhysicalFileKey.FileId.Low:X16}:{content.ByteLength}:{content.Sha256}");
}
