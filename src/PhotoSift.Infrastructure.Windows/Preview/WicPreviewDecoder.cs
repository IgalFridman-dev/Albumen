using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scope;

namespace PhotoSift.Infrastructure.Windows.Preview;

public sealed class WicPreviewDecoder
{
    private readonly WicCodecCapability codecs;

    public const string UnsupportedReason =
        "Preview unavailable: this file format is not supported.";
    public const string CodecUnavailableReason =
        "Preview unavailable: the required Windows codec is not installed.";
    public const string CouldNotAnalyzeReason =
        "Preview unavailable: the image could not be analyzed.";
    public const string FileUnavailableReason =
        "Preview unavailable: the file is unavailable.";
    public const string ChangedReason =
        "Preview unavailable: the file changed while it was analyzed.";

    public WicPreviewDecoder() : this(new WicCodecCapability(new ComClassAvailability())) { }

    internal WicPreviewDecoder(WicCodecCapability codecs) =>
        this.codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));

    internal Task<PreviewDecodeResult> DecodeAsync(
        string fullPath, int maximumEdge, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEdge);
        return Task.Run(() => Decode(fullPath, maximumEdge, cancellationToken), cancellationToken);
    }

    public Task<PreviewDecodeResult> DecodeAsync(
        IProtectedFileLease lease,
        string fullPath,
        int maximumEdge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEdge);
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = Recognize(fullPath);
            if (format is null)
                return PreviewDecodeResult.Failure(
                    fullPath, PreviewAvailability.Unsupported, UnsupportedReason);
            if (format is SupportedImageFormat.Heic && !codecs.IsAvailable(format.Value))
                return PreviewDecodeResult.Failure(
                    fullPath, PreviewAvailability.Unsupported, CodecUnavailableReason);

            try
            {
                lease.Content.Position = 0;
                var result = DecodeContent(
                    lease.Content, fullPath, maximumEdge, cancellationToken);
                try
                {
                    var final = await lease.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    var opened = lease.OpenedFacts;
                    if (final.PhysicalFileKey != opened.PhysicalFileKey ||
                        final.ByteLength != opened.ByteLength)
                    {
                        result.Dispose();
                        return PreviewDecodeResult.Failure(
                            fullPath, PreviewAvailability.CouldNotAnalyze, ChangedReason);
                    }
                    return result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsExpectedDecodeFailure(exception))
            {
                return FailureFromException(fullPath, exception);
            }
        }, cancellationToken);
    }

    private PreviewDecodeResult Decode(
        string fullPath, int maximumEdge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var format = Recognize(fullPath);
        if (format is null)
            return PreviewDecodeResult.Failure(
                fullPath, PreviewAvailability.Unsupported, UnsupportedReason);
        if (format is SupportedImageFormat.Heic && !codecs.IsAvailable(format.Value))
            return PreviewDecodeResult.Failure(
                fullPath, PreviewAvailability.Unsupported, CodecUnavailableReason);
        if (!File.Exists(fullPath))
            return PreviewDecodeResult.Failure(
                fullPath, PreviewAvailability.Unavailable, FileUnavailableReason);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return DecodeContent(stream, fullPath, maximumEdge, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsExpectedDecodeFailure(exception))
        {
            return FailureFromException(fullPath, exception);
        }
    }

    private static PreviewDecodeResult DecodeContent(
        Stream stream, string fullPath, int maximumEdge, CancellationToken cancellationToken)
    {
        var header = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None).Frames[0];
        var orientation = ReadOrientation(header.Metadata);
        var sourceWidth = header.PixelWidth;
        var sourceHeight = header.PixelHeight;
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        if (sourceWidth >= sourceHeight && sourceWidth > maximumEdge)
            bitmap.DecodePixelWidth = maximumEdge;
        else if (sourceHeight > maximumEdge)
            bitmap.DecodePixelHeight = maximumEdge;
        bitmap.EndInit();
        bitmap.Freeze();

        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return PreviewDecodeResult.Success(fullPath,
            CopyOriented(bgra, orientation, cancellationToken));
    }

    private static bool IsExpectedDecodeFailure(Exception exception) => exception is
        FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or
        IOException or NotSupportedException or FileFormatException or COMException;

    private static PreviewDecodeResult FailureFromException(string fullPath, Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
            ? PreviewDecodeResult.Failure(
                fullPath, PreviewAvailability.Unavailable, FileUnavailableReason)
            : PreviewDecodeResult.Failure(
                fullPath, PreviewAvailability.CouldNotAnalyze, CouldNotAnalyzeReason);

    private static PreviewFrame CopyOriented(
        BitmapSource source, ushort orientation, CancellationToken cancellationToken)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var length = checked(width * height * 4);
        var sourcePixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            source.CopyPixels(sourcePixels, width * 4, 0);
            var swapsAxes = orientation is 5 or 6 or 7 or 8;
            var output = PreviewFrame.Rent(swapsAxes ? height : width, swapsAxes ? width : height);
            try
            {
                var target = output.WritablePixels.Span;
                for (var y = 0; y < output.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < output.Width; x++)
                    {
                        var (sourceX, sourceY) = MapToSource(x, y, width, height, orientation);
                        var sourceOffset = ((sourceY * width) + sourceX) * 4;
                        var targetOffset = ((y * output.Width) + x) * 4;
                        sourcePixels.AsSpan(sourceOffset, 4).CopyTo(target[targetOffset..]);
                    }
                }
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourcePixels, clearArray: true);
        }
    }

    private static (int X, int Y) MapToSource(
        int x, int y, int width, int height, ushort orientation) => orientation switch
    {
        2 => (width - 1 - x, y),
        3 => (width - 1 - x, height - 1 - y),
        4 => (x, height - 1 - y),
        5 => (y, x),
        6 => (y, height - 1 - x),
        7 => (width - 1 - y, height - 1 - x),
        8 => (width - 1 - y, x),
        _ => (x, y),
    };

    private static ushort ReadOrientation(object? metadata)
    {
        if (metadata is not BitmapMetadata bitmapMetadata) return 1;
        try
        {
            var value = bitmapMetadata.GetQuery("/app1/ifd/{ushort=274}");
            var orientation = value switch
            {
                ushort number => number,
                uint number when number <= ushort.MaxValue => (ushort)number,
                _ => (ushort)1,
            };
            return orientation is >= 1 and <= 8 ? orientation : (ushort)1;
        }
        catch (NotSupportedException) { return 1; }
    }

    private static SupportedImageFormat? Recognize(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JPG" or ".JPEG" => SupportedImageFormat.Jpeg,
            ".PNG" => SupportedImageFormat.Png,
            ".HEIC" or ".HEIF" => SupportedImageFormat.Heic,
            _ => null,
        };
}
