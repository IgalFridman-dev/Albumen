using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Preview;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Preview;

public sealed class BoundedReviewPreviewSourceTests
{
    [Fact]
    public async Task DecodeStartsOffTheCallerThreadAndReturnsOpenedEvidence()
    {
        var png = CreatePng();
        var openedDate = new DateTimeOffset(2026, 9, 16, 10, 30, 0, TimeSpan.Zero);
        var reader = new RecordingReader(png, openedDate);
        using var cache = new BoundedThumbnailCache(256);
        await using var source = new BoundedReviewPreviewSource(
            reader, new WicPreviewDecoder(), cache, maximumEdge: 2);
        var completion = new TaskCompletionSource<ReviewPreviewResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        var callerThread = 0;
        var thread = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try
            {
                completion.SetResult(source.LoadAsync(
                    Context(), Occurrence(1, png, 'A'),
                    cancellationToken).GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }) { IsBackground = true };
        thread.Start();

        using var result = await completion.Task.WaitAsync(cancellationToken);
        thread.Join();

        Assert.Equal(PreviewAvailability.Available, result.Availability);
        Assert.NotEqual(callerThread, reader.DecodeThreadId);
        Assert.Equal(2, result.PixelWidth);
        Assert.Equal(2, result.PixelHeight);
        Assert.Equal(png.LongLength, result.ByteLength);
        Assert.Equal(openedDate, result.RelevantDate);
    }

    [Fact]
    public async Task RepeatedNavigationNeverLetsCacheOwnMoreThanItsByteCapacity()
    {
        var png = CreatePng();
        using var pool = new TrackingMemoryPool(extraBytesPerOwner: 31);
        var decoder = new WicPreviewDecoder(
            new WicCodecCapability(new AlwaysAvailable()), pool);
        var ownerBytes = 2 * 2 * 4 + 31;
        using var cache = new BoundedThumbnailCache(ownerBytes);
        await using (var source = new BoundedReviewPreviewSource(
            new RecordingReader(png, DateTimeOffset.UnixEpoch),
            decoder, cache, maximumEdge: 2))
        {
            for (var index = 0; index < 8; index++)
            {
                using var result = await source.LoadAsync(
                    Context(), Occurrence(index + 1, png, (char)('0' + index)),
                    TestContext.Current.CancellationToken);

                Assert.True(cache.OwnedBytes <= ownerBytes);
            }
        }

        Assert.Equal(0, cache.OwnedBytes);
        Assert.Equal(0, pool.CurrentBytes);
    }

    private static ScanRunContext Context() => new(
        new(Guid.Parse("61000000-0000-0000-0000-000000000001"), 7, 3, 5),
        new(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"\\?\C:\Photos",
            new(7, 0, 70)));

    private static ScanFileOccurrence Occurrence(long id, byte[] png, char digestCharacter)
    {
        var context = Context();
        var key = new PhysicalFileKey(7, new(0, checked((ulong)id)));
        var path = $@"C:\Photos\{id}.png";
        return new(new(context.Pin, id, 2, 3), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None),
            new(key, png.LongLength, new string(digestCharacter, 64)),
            new(png.LongLength, 1, true));
    }

    private static byte[] CreatePng()
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32,
            null, new byte[16], 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class RecordingReader(byte[] content, DateTimeOffset openedDate) : IProtectedFileReader
    {
        private int decodeThreadId;
        public int DecodeThreadId => Volatile.Read(ref decodeThreadId);

        public ValueTask<IProtectedFileLease> OpenAsync(
            ProtectedOpenRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new RecordingStream(content,
                threadId => Interlocked.CompareExchange(ref decodeThreadId, threadId, 0));
            return ValueTask.FromResult<IProtectedFileLease>(new RecordingLease(
                stream,
                new(request.ExpectedPhysicalFileKey, content.LongLength,
                    request.ResolvedPath, openedDate, new(content.LongLength, 1, true))));
        }
    }

    private sealed class RecordingLease(Stream stream, ProtectedFileFacts facts) : IProtectedFileLease
    {
        public Stream Content { get; } = stream;
        public ProtectedFileFacts OpenedFacts { get; } = facts;
        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(OpenedFacts);
        }
        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }

    private sealed class RecordingStream(byte[] content, Action<int> read) : MemoryStream(content, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            read(Environment.CurrentManagedThreadId);
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            read(Environment.CurrentManagedThreadId);
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            read(Environment.CurrentManagedThreadId);
            return base.ReadByte();
        }
    }

    private sealed class AlwaysAvailable : IComClassAvailability
    {
        public bool IsAvailable(Guid classId) => true;
    }

    private sealed class TrackingMemoryPool(int extraBytesPerOwner) : MemoryPool<byte>
    {
        private long currentBytes;
        public override int MaxBufferSize => int.MaxValue;
        public long CurrentBytes => Interlocked.Read(ref currentBytes);

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minBufferSize);
            var length = checked(minBufferSize + extraBytesPerOwner);
            Interlocked.Add(ref currentBytes, length);
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
