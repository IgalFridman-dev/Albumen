using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Siglip2Spike;

/// <summary>The app's Near-stage decode (WicPreviewDecoder): WIC scaled to a maximum edge, BGRA, EXIF-oriented.</summary>
public static class WicDecode
{
    public const int MaximumEdge = 256;

    public static (byte[] Bgra, int Width, int Height) Decode(string path, int maximumEdge)
    {
        using var stream = File.OpenRead(path);
        var header = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None).Frames[0];
        var orientation = Orientation(header.Metadata as BitmapMetadata);
        var (sourceWidth, sourceHeight) = (header.PixelWidth, header.PixelHeight);
        stream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        if (sourceWidth >= sourceHeight && sourceWidth > maximumEdge) bitmap.DecodePixelWidth = maximumEdge;
        else if (sourceHeight > maximumEdge) bitmap.DecodePixelHeight = maximumEdge;
        bitmap.EndInit();
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        var (width, height) = (bgra.PixelWidth, bgra.PixelHeight);
        var source = new byte[width * height * 4];
        bgra.CopyPixels(source, width * 4, 0);
        var swaps = orientation is 5 or 6 or 7 or 8;
        var (outWidth, outHeight) = swaps ? (height, width) : (width, height);
        var output = new byte[source.Length];
        for (var y = 0; y < outHeight; y++)
            for (var x = 0; x < outWidth; x++)
            {
                var (sx, sy) = orientation switch
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
                Array.Copy(source, ((sy * width) + sx) * 4, output, ((y * outWidth) + x) * 4, 4);
            }
        return (output, outWidth, outHeight);
    }

    private static int Orientation(BitmapMetadata? metadata)
    {
        try
        {
            return metadata?.GetQuery("/app1/ifd/{ushort=274}") switch
            {
                ushort value and >= 1 and <= 8 => value,
                _ => 1,
            };
        }
        catch (NotSupportedException) { return 1; }
    }
}
