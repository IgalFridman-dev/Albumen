using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Analysis.Exact;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Infrastructure.Tests.Preview;

public sealed class WicEncoderImageSourceTests
{
    [Fact]
    public async Task APhotoWithItsScannedContentIsDecodedWithinTheEdge()
    {
        var png = CreatePng(600, 300);

        var image = await Source(png).ReadAsync(Context(), Occurrence(png, Digest(png)), 512,
            TestContext.Current.CancellationToken);

        Assert.NotNull(image);
        Assert.Equal((512, 256), (image.Width, image.Height));
        Assert.Equal(512 * 256 * 4, image.Bgra.Length);
    }

    [Fact]
    public async Task ASmallPhotoIsNeverEnlarged()
    {
        var png = CreatePng(40, 30);

        var image = await Source(png).ReadAsync(Context(), Occurrence(png, Digest(png)), 512,
            TestContext.Current.CancellationToken);

        Assert.Equal((40, 30), (image!.Width, image.Height));
    }

    [Fact]
    public async Task APhotoThatChangedSinceTheScanIsNotDecoded()
    {
        var png = CreatePng(40, 30);

        var image = await Source(png).ReadAsync(Context(), Occurrence(png, new string('0', 64)), 512,
            TestContext.Current.CancellationToken);

        Assert.Null(image);
    }

    [Fact]
    public async Task APhotoWithoutProvenContentIsNotOpened()
    {
        var png = CreatePng(40, 30);
        var row = Occurrence(png, Digest(png));
        var unproven = new ScanFileOccurrence(row.Token, row.DiscoveredPath, row.ResolvedPath, row.PhysicalFileKey,
            row.Enumerate, new(ScanStageState.FailedRetryable, ScanReason.ReadFailure), null, row.Allocation);

        var image = await new WicEncoderImageSource(new UnusedReader(), new StreamingExactContentAnalyzer(),
            new WicPreviewDecoder()).ReadAsync(Context(), unproven, 512, TestContext.Current.CancellationToken);

        Assert.Null(image);
    }

    private static WicEncoderImageSource Source(byte[] content) =>
        new(new MemoryReader(content), new StreamingExactContentAnalyzer(), new WicPreviewDecoder());

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static ScanRunContext Context() => new(
        new(Guid.Parse("62000000-0000-0000-0000-000000000001"), 1, 1, 1),
        new(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"C:\Photos", new(7, 0, 70)));

    private static ScanFileOccurrence Occurrence(byte[] png, string digest)
    {
        var key = new PhysicalFileKey(7, new(0, 1));
        return new(new(Context().Pin, 1, 1, 1), @"C:\Photos\1.png", @"C:\Photos\1.png", key,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None),
            new(key, png.LongLength, digest), new(png.LongLength, 1, true));
    }

    private static byte[] CreatePng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class MemoryReader(byte[] content) : IProtectedFileReader
    {
        public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IProtectedFileLease>(new MemoryLease(new MemoryStream(content, writable: false),
                new(request.ExpectedPhysicalFileKey, content.LongLength, request.ResolvedPath, DateTimeOffset.UnixEpoch,
                    new(content.LongLength, 1, true))));
    }

    private sealed class UnusedReader : IProtectedFileReader
    {
        public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A photo without proven content must not be opened.");
    }

    private sealed class MemoryLease(Stream stream, ProtectedFileFacts facts) : IProtectedFileLease
    {
        public Stream Content { get; } = stream;
        public ProtectedFileFacts OpenedFacts { get; } = facts;
        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(OpenedFacts);
        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }
}
