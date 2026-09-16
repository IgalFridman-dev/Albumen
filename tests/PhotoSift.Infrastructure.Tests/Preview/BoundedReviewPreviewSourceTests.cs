using System.Buffers;
using System.Security.Cryptography;
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

    [Theory]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.Missing,
        PreviewAvailability.Unavailable, "Preview unavailable: the file is unavailable.")]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.AccessDenied,
        PreviewAvailability.Unavailable, "Preview unavailable: access to the file was denied.")]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.Changed,
        PreviewAvailability.CouldNotAnalyze, "Preview unavailable: the file changed while it was analyzed.")]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation,
        PreviewAvailability.Unavailable, "Preview unavailable: the file is outside the approved Scope.")]
    public async Task ProtectedOpenFailureReturnsLiteralAccessibleOutcome(
        ScanStageState state,
        ScanReason reason,
        PreviewAvailability availability,
        string expectedMessage)
    {
        var png = CreatePng();
        await using var source = new BoundedReviewPreviewSource(
            new ThrowingReader(new ScanReadException(new(state, reason), "protected open failed")),
            new WicPreviewDecoder(), new BoundedThumbnailCache(256), maximumEdge: 2);

        using var result = await source.LoadAsync(
            Context(), Occurrence(1, png, 'A'), TestContext.Current.CancellationToken);

        Assert.Equal(availability, result.Availability);
        Assert.Equal(expectedMessage, result.Reason);
        Assert.Null(result.Frame);
    }

    [Fact]
    public async Task SameLengthMutationCannotReturnTheCachedPixelsForThePinnedDigest()
    {
        var original = CreatePng();
        var changed = original.ToArray();
        changed[^12] ^= 0x01;
        var reader = new MutableReader(original);
        using var cache = new BoundedThumbnailCache(256);
        await using var source = new BoundedReviewPreviewSource(
            reader, new WicPreviewDecoder(), cache, maximumEdge: 2);
        var occurrence = Occurrence(1, original,
            Convert.ToHexString(SHA256.HashData(original)));

        using (var first = await source.LoadAsync(
            Context(), occurrence, TestContext.Current.CancellationToken))
            Assert.Equal(PreviewAvailability.Available, first.Availability);

        reader.Content = changed;
        using var second = await source.LoadAsync(
            Context(), occurrence, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewAvailability.CouldNotAnalyze, second.Availability);
        Assert.Equal(WicPreviewDecoder.ChangedReason, second.Reason);
        Assert.Null(second.Frame);
    }

    [Fact]
    public async Task DisposalWaitsForAnInFlightLoadBeforeReleasingTheCache()
    {
        var png = CreatePng();
        var reader = new BlockingReader(png);
        var cache = new BoundedThumbnailCache(256);
        var source = new BoundedReviewPreviewSource(
            reader, new WicPreviewDecoder(), cache, maximumEdge: 2);
        var load = source.LoadAsync(
            Context(), Occurrence(1, png, 'A'), TestContext.Current.CancellationToken);
        await reader.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var disposal = source.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        reader.Release.TrySetResult(true);

        using var result = await load;
        await disposal;
        Assert.Equal(0, cache.OwnedBytes);
    }

    private static ScanRunContext Context() => new(
        new(Guid.Parse("61000000-0000-0000-0000-000000000001"), 7, 3, 5),
        new(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"\\?\C:\Photos",
            new(7, 0, 70)));

    private static ScanFileOccurrence Occurrence(long id, byte[] png, char digestCharacter)
    {
        _ = digestCharacter;
        return Occurrence(id, png, Convert.ToHexString(SHA256.HashData(png)));
    }

    private static ScanFileOccurrence Occurrence(long id, byte[] png, string digest)
    {
        var context = Context();
        var key = new PhysicalFileKey(7, new(0, checked((ulong)id)));
        var path = $@"C:\Photos\{id}.png";
        return new(new(context.Pin, id, 2, 3), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None),
            new(key, png.LongLength, digest),
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

    private sealed class ThrowingReader(Exception exception) : IProtectedFileReader
    {
        public ValueTask<IProtectedFileLease> OpenAsync(
            ProtectedOpenRequest request,
            CancellationToken cancellationToken) => ValueTask.FromException<IProtectedFileLease>(exception);
    }

    private sealed class MutableReader(byte[] content) : IProtectedFileReader
    {
        public byte[] Content { get; set; } = content;

        public ValueTask<IProtectedFileLease> OpenAsync(
            ProtectedOpenRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Content;
            return ValueTask.FromResult<IProtectedFileLease>(new RecordingLease(
                new MemoryStream(current, writable: false),
                new(request.ExpectedPhysicalFileKey, current.LongLength,
                    request.ResolvedPath, DateTimeOffset.UnixEpoch,
                    new(current.LongLength, 1, true))));
        }
    }

    private sealed class BlockingReader(byte[] content) : IProtectedFileReader
    {
        public TaskCompletionSource<bool> Entered { get; } = NewSignal();
        public TaskCompletionSource<bool> Release { get; } = NewSignal();

        public async ValueTask<IProtectedFileLease> OpenAsync(
            ProtectedOpenRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new RecordingLease(new MemoryStream(content, writable: false),
                new(request.ExpectedPhysicalFileKey, content.LongLength,
                    request.ResolvedPath, DateTimeOffset.UnixEpoch,
                    new(content.LongLength, 1, true)));
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
