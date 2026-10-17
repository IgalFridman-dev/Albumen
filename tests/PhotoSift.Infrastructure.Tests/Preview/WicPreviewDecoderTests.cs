using System.Buffers;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Preview;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Preview;

public sealed class WicPreviewDecoderTests
{
    private static readonly byte[][] SourceColors =
    [
        [0, 0, 240, 255], [0, 240, 0, 255],
        [240, 0, 0, 255], [0, 240, 240, 255],
        [240, 0, 240, 255], [240, 240, 0, 255],
    ];

    public static TheoryData<ushort, int, int, int[]> Orientations => new()
    {
        { 1, 2, 3, [0, 1, 2, 3, 4, 5] },
        { 2, 2, 3, [1, 0, 3, 2, 5, 4] },
        { 3, 2, 3, [5, 4, 3, 2, 1, 0] },
        { 4, 2, 3, [4, 5, 2, 3, 0, 1] },
        { 5, 3, 2, [0, 2, 4, 1, 3, 5] },
        { 6, 3, 2, [4, 2, 0, 5, 3, 1] },
        { 7, 3, 2, [5, 3, 1, 4, 2, 0] },
        { 8, 3, 2, [1, 3, 5, 0, 2, 4] },
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task JpegExifOrientationProducesTheSameOrientedAnalysisAndDisplayPixels(
        ushort orientation, int expectedWidth, int expectedHeight, int[] expectedCells)
    {
        using var fixture = JpegFixture.Create(orientation);
        var before = ReadSourceTruth(fixture.Path);

        using var result = await new WicPreviewDecoder().DecodeAsync(
            fixture.Path, 256, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.Available, result.Availability);
        Assert.NotNull(result.Frame);
        Assert.Equal(expectedWidth * JpegFixture.CellSize, result.Frame.Width);
        Assert.Equal(expectedHeight * JpegFixture.CellSize, result.Frame.Height);
        Assert.Equal(result.Frame.DisplayPixels.ToArray(), result.Frame.AnalysisPixels.ToArray());
        for (var index = 0; index < expectedCells.Length; index++)
        {
            var x = (index % expectedWidth) * JpegFixture.CellSize + (JpegFixture.CellSize / 2);
            var y = (index / expectedWidth) * JpegFixture.CellSize + (JpegFixture.CellSize / 2);
            AssertNearestColor(result.Frame, x, y, expectedCells[index]);
        }
        Assert.Equal(before, ReadSourceTruth(fixture.Path));
    }

    [Fact]
    public async Task PngIsDecodedThroughWicAndBoundedToTheRequestedEdge()
    {
        using var fixture = PngFixture.Create(width: 320, height: 160);
        var before = ReadSourceTruth(fixture.Path);

        using var result = await new WicPreviewDecoder().DecodeAsync(
            fixture.Path, 80, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.Available, result.Availability);
        Assert.NotNull(result.Frame);
        Assert.Equal(80, result.Frame.Width);
        Assert.Equal(40, result.Frame.Height);
        Assert.Equal(80 * 40 * 4, result.Frame.ByteLength);
        Assert.Equal(before, ReadSourceTruth(fixture.Path));
    }

    [Fact]
    public async Task BoundedPreviewPreservesTheOriginalOrientedSourceDimensions()
    {
        using var fixture = PngFixture.Create(width: 6000, height: 4000);

        using var result = await new WicPreviewDecoder().DecodeAsync(
            fixture.Path, 512, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.Available, result.Availability);
        Assert.Equal(6000, result.SourcePixelWidth);
        Assert.Equal(4000, result.SourcePixelHeight);
        Assert.Equal(512, result.Frame?.Width);
        Assert.Equal(341, result.Frame?.Height);
    }

    [Fact]
    public async Task MissingOptionalHeifCodecIsUnsupportedWithoutDownloadingAnything()
    {
        using var directory = TemporaryDirectory.Create();
        var path = System.IO.Path.Combine(directory.Path, "optional.heic");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var capability = new WicCodecCapability(new FixedComClassAvailability(available: false));

        using var result = await new WicPreviewDecoder(capability).DecodeAsync(
            path, 128, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.Unsupported, result.Availability);
        Assert.Equal("Preview unavailable: the required Windows codec is not installed.",
            result.Reason);
    }

    [Fact]
    public async Task LargeDecodeLeavesDispatcherResponsiveAndReleasesTheSourceHandle()
    {
        using var fixture = PngFixture.Create(width: 4096, height: 4096);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var decode = new WicPreviewDecoder().DecodeAsync(
                        fixture.Path, 128, TestContext.Current.CancellationToken);
                    await dispatcher.InvokeAsync(static () => { });
                    var dispatcherRanDuringDecode = !decode.IsCompleted;
                    using var result = await decode;
                    using var exclusive = new FileStream(fixture.Path, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None);
                    completion.SetResult(dispatcherRanDuringDecode &&
                        result.Availability is PreviewAvailability.Available);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(await completion.Task.WaitAsync(TestContext.Current.CancellationToken));
        thread.Join();
    }

    [Fact]
    public async Task ProtectedLeaseIsRefreshedAfterWicMaterializationAndRemainsCallerOwned()
    {
        using var fixture = PngFixture.Create(width: 32, height: 16);
        await using var lease = new RecordingProtectedLease(fixture.Path);

        using var result = await new WicPreviewDecoder().DecodeAsync(
            lease, fixture.Path, 16, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.Available, result.Availability);
        Assert.Equal(1, lease.RefreshCount);
        Assert.True(lease.Content.CanRead);
    }

    [Fact]
    public async Task RepeatedLargeImageNavigationHasMeasuredOwnedMemoryBound()
    {
        using var fixture = PngFixture.Create(width: 2048, height: 2048);
        using var pool = new TrackingMemoryPool(extraBytesPerOwner: 257);
        var capability = new WicCodecCapability(new FixedComClassAvailability(available: true));
        var decoder = new WicPreviewDecoder(capability, pool);
        var ownerBytes = (128 * 128 * 4) + 257;
        var capacity = ownerBytes * 2L;
        var trace = new List<(long CacheBytes, long RentedBytes)>();

        using (var cache = new BoundedThumbnailCache(capacity))
        {
            for (var index = 0; index < 8; index++)
            {
                using var result = await decoder.DecodeAsync(
                    fixture.Path, 128, TestContext.Current.CancellationToken);
                cache.Add($"navigation-{index}", result.TakeFrame());
                trace.Add((cache.OwnedBytes, pool.CurrentBytes));
            }

            Assert.All(trace, sample =>
            {
                Assert.True(sample.CacheBytes <= capacity);
                Assert.Equal(sample.CacheBytes, sample.RentedBytes);
            });
            Assert.True(pool.PeakBytes <= capacity + ownerBytes);
        }

        Assert.Equal(0, pool.CurrentBytes);
    }

    [Theory]
    [InlineData("broken.jpg", PreviewAvailability.CouldNotAnalyze,
        "Preview unavailable: the image could not be analyzed.")]
    [InlineData("broken.png", PreviewAvailability.CouldNotAnalyze,
        "Preview unavailable: the image could not be analyzed.")]
    [InlineData("missing.jpg", PreviewAvailability.Unavailable,
        "Preview unavailable: the file is unavailable.")]
    [InlineData("image.gif", PreviewAvailability.Unsupported,
        "Preview unavailable: this file format is not supported.")]
    public async Task FailureHasTypedOutcomeAndLiteralAccessibleReason(
        string fileName, PreviewAvailability expected, string expectedReason)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, fileName);
        if (!fileName.StartsWith("missing", StringComparison.Ordinal))
            await File.WriteAllTextAsync(
                path, "not an image", TestContext.Current.CancellationToken);
        var before = expected is PreviewAvailability.CouldNotAnalyze
            ? ReadSourceTruth(path)
            : null;

        using var result = await new WicPreviewDecoder().DecodeAsync(
            path, 128, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Availability);
        Assert.Equal(path, result.FullPath);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Null(result.Frame);
        if (before is not null)
        {
            Assert.Equal(before, ReadSourceTruth(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JpegCutInsideItsImageDataCouldNotBeAnalyzedInsteadOfDecodingAsGray(bool throughLease)
    {
        using var fixture = JpegFixture.Create(orientation: 1);
        var bytes = await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken);
        var scan = bytes.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xDA]);
        var imageData = scan + 2 + ((bytes[scan + 2] << 8) | bytes[scan + 3]);
        var cut = (imageData + bytes.Length - 2) / 2;
        Assert.InRange(cut, imageData + 1, bytes.Length - 3);
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "cut-off.jpg");
        await File.WriteAllBytesAsync(path, bytes[..cut], TestContext.Current.CancellationToken);

        PreviewDecodeResult result;
        if (throughLease)
        {
            await using var lease = new RecordingProtectedLease(path);
            result = await new WicPreviewDecoder().DecodeAsync(
                lease, path, 256, TestContext.Current.CancellationToken);
            Assert.Equal(1, lease.RefreshCount);
        }
        else
        {
            result = await new WicPreviewDecoder().DecodeAsync(
                path, 256, TestContext.Current.CancellationToken);
        }

        using (result)
        {
            Assert.Equal(PreviewAvailability.CouldNotAnalyze, result.Availability);
            Assert.Equal("Preview unavailable: the photo file is incomplete.", result.Reason);
            Assert.Null(result.Frame);
        }
    }

    [Fact]
    public async Task JpegWithoutItsEndMarkerCouldNotBeAnalyzed()
    {
        // A file cut exactly after its last scan cannot be told apart from one whose end
        // marker was never written, so both are reported rather than guessed complete.
        using var fixture = JpegFixture.Create(orientation: 1);
        var bytes = await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken);
        Assert.Equal((byte)0xFF, bytes[^2]);
        Assert.Equal((byte)0xD9, bytes[^1]);
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "no-end-marker.jpg");
        await File.WriteAllBytesAsync(path, bytes[..^2], TestContext.Current.CancellationToken);

        using var result = await new WicPreviewDecoder().DecodeAsync(
            path, 256, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.CouldNotAnalyze, result.Availability);
        Assert.Equal("Preview unavailable: the photo file is incomplete.", result.Reason);
    }

    [Fact]
    public async Task CompleteJpegWithDataAfterTheImageStillDecodes()
    {
        using var fixture = JpegFixture.Create(orientation: 1);
        var bytes = await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken);
        var trailer = Enumerable.Range(0, 4096).Select(index => (byte)(index * 37)).ToArray();
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "motion-photo.jpg");
        await File.WriteAllBytesAsync(path, [.. bytes, .. trailer], TestContext.Current.CancellationToken);

        using var result = await new WicPreviewDecoder().DecodeAsync(
            path, 256, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.Available, result.Availability);
        Assert.NotNull(result.Frame);
        AssertNearestColor(result.Frame, JpegFixture.CellSize / 2, JpegFixture.CellSize / 2, 0);
    }

    private static SourceTruth ReadSourceTruth(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var facts = new WindowsFileNative().Read(stream.SafeFileHandle, path);
        var key = facts.PhysicalFileKey
            ?? throw new InvalidDataException($"Source identity was unavailable: {path}");
        return new SourceTruth(key, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void AssertNearestColor(
        PreviewFrame frame, int x, int y, int expectedColorIndex)
    {
        var offset = ((y * frame.Width) + x) * 4;
        var pixels = frame.DisplayPixels.Span;
        var closest = -1;
        var closestDistance = int.MaxValue;
        for (var color = 0; color < SourceColors.Length; color++)
        {
            var distance = 0;
            for (var channel = 0; channel < 3; channel++)
            {
                var difference = pixels[offset + channel] - SourceColors[color][channel];
                distance += difference * difference;
            }
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closest = color;
        }
        Assert.Equal(expectedColorIndex, closest);
    }

    private sealed class JpegFixture : IDisposable
    {
        public const int CellSize = 32;
        private readonly TemporaryDirectory directory;

        private JpegFixture(TemporaryDirectory directory, string path)
        {
            this.directory = directory;
            Path = path;
        }

        public string Path { get; }

        public static JpegFixture Create(ushort orientation)
        {
            var directory = TemporaryDirectory.Create();
            var path = System.IO.Path.Combine(directory.Path, $"orientation-{orientation}.jpg");
            var pixels = new byte[2 * CellSize * 3 * CellSize * 4];
            for (var cell = 0; cell < SourceColors.Length; cell++)
            {
                var cellX = cell % 2;
                var cellY = cell / 2;
                for (var y = 0; y < CellSize; y++)
                for (var x = 0; x < CellSize; x++)
                    SourceColors[cell].CopyTo(pixels, ((((cellY * CellSize) + y) * 2 * CellSize) +
                        (cellX * CellSize) + x) * 4);
            }

            var source = BitmapSource.Create(2 * CellSize, 3 * CellSize, 96, 96,
                PixelFormats.Bgra32, null, pixels, 2 * CellSize * 4);
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return new(directory, path);
        }

        public void Dispose() => directory.Dispose();
    }

    private sealed record SourceTruth(PhysicalFileKey PhysicalFileKey, string Sha256);

    private sealed class PngFixture : IDisposable
    {
        private readonly TemporaryDirectory directory;
        private PngFixture(TemporaryDirectory directory, string path)
        {
            this.directory = directory;
            Path = path;
        }
        public string Path { get; }

        public static PngFixture Create(int width, int height)
        {
            var directory = TemporaryDirectory.Create();
            var path = System.IO.Path.Combine(directory.Path, "large.png");
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, new byte[width * height * 4], width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return new(directory, path);
        }

        public void Dispose() => directory.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "PhotoSift-E3S1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FixedComClassAvailability(bool available) : IComClassAvailability
    {
        public bool IsAvailable(Guid classId) => available;
    }

    private sealed class RecordingProtectedLease : IProtectedFileLease
    {
        private readonly FileStream stream;

        public RecordingProtectedLease(string path)
        {
            stream = File.OpenRead(path);
            OpenedFacts = new ProtectedFileFacts(new PhysicalFileKey(1, new FileId128(2, 3)),
                stream.Length, path, File.GetLastWriteTimeUtc(path),
                new AllocationEvidence(stream.Length, 1, true));
        }

        public Stream Content => stream;
        public ProtectedFileFacts OpenedFacts { get; }
        public int RefreshCount { get; private set; }

        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            return ValueTask.FromResult(OpenedFacts);
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private sealed class TrackingMemoryPool(int extraBytesPerOwner) : MemoryPool<byte>
    {
        private long currentBytes;
        private long peakBytes;
        public override int MaxBufferSize => int.MaxValue;
        public long CurrentBytes => Interlocked.Read(ref currentBytes);
        public long PeakBytes => Interlocked.Read(ref peakBytes);

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minBufferSize);
            var length = checked(minBufferSize + extraBytesPerOwner);
            var current = Interlocked.Add(ref currentBytes, length);
            while (true)
            {
                var peak = Interlocked.Read(ref peakBytes);
                if (current <= peak || Interlocked.CompareExchange(
                    ref peakBytes, current, peak) == peak) break;
            }
            return new TrackingOwner(length,
                () => Interlocked.Add(ref currentBytes, -length));
        }

        protected override void Dispose(bool disposing) { }

        private sealed class TrackingOwner(int length, Action released) : IMemoryOwner<byte>
        {
            private byte[]? bytes = new byte[length];
            public Memory<byte> Memory => bytes ??
                throw new ObjectDisposedException(nameof(TrackingOwner));
            public void Dispose()
            {
                if (Interlocked.Exchange(ref bytes, null) is null) return;
                released();
            }
        }
    }
}
