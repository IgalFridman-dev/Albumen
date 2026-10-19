using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Infrastructure.Tests.Preview;

public sealed class BoundedThumbnailCacheTests
{
    [Fact]
    public void CacheChargesActualPooledOwnerBytesRatherThanLogicalPixelBytes()
    {
        using var cache = new BoundedThumbnailCache(byteCapacity: 48);
        var first = new PreviewFrame(2, 2, new OversizedMemoryOwner(32));
        var second = new PreviewFrame(2, 2, new OversizedMemoryOwner(32));
        cache.Add("first", first);
        cache.Add("second", second);

        Assert.Equal(32, second.OwnedByteLength);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Equal(32, cache.OwnedBytes);
    }

    [Fact]
    public async Task DisposeReleasesEveryCachedFrame()
    {
        using var fixture = PngFixture.Create();
        var cache = new BoundedThumbnailCache(byteCapacity: 64);
        var first = await DecodeFrameAsync(fixture.Path);
        var second = await DecodeFrameAsync(fixture.Path);
        cache.Add("first", first);
        cache.Add("second", second);

        cache.Dispose();

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Equal(0, cache.OwnedBytes);
    }

    private static async Task<PreviewFrame> DecodeFrameAsync(string path)
    {
        using var result = await new WicPreviewDecoder().DecodeAsync(
            path, 2, TestContext.Current.CancellationToken);
        Assert.Equal(PreviewAvailability.Available, result.Availability);
        return result.TakeFrame();
    }

    private sealed class PngFixture : IDisposable
    {
        private PngFixture(string directory, string path)
        {
            DirectoryPath = directory;
            Path = path;
        }

        private string DirectoryPath { get; }
        public string Path { get; }

        public static PngFixture Create()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "Albumen-E3S1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "cache.png");
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32,
                null, new byte[16], 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return new(directory, path);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class OversizedMemoryOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? bytes = new byte[length];
        public Memory<byte> Memory => bytes ??
            throw new ObjectDisposedException(nameof(OversizedMemoryOwner));
        public void Dispose() => bytes = null;
    }
}
