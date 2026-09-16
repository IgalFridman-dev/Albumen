using System.Buffers;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Windows.Preview;

public enum PreviewAvailability
{
    Available,
    Unsupported,
    CouldNotAnalyze,
    Unavailable,
}

public interface IReviewPreviewSource : IAsyncDisposable
{
    Task<ReviewPreviewResult> LoadAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken);
}

public sealed class ReviewPreviewResult : IDisposable
{
    private PreviewFrame? frame;
    private int disposed;

    private ReviewPreviewResult(
        string fullPath,
        PreviewAvailability availability,
        string reason,
        PreviewFrame? frame,
        long? byteLength,
        DateTimeOffset? relevantDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(reason);
        if ((availability is PreviewAvailability.Available) != (frame is not null))
            throw new ArgumentException("Only an available preview can contain pixels.", nameof(frame));

        FullPath = fullPath;
        Availability = availability;
        Reason = reason;
        this.frame = frame;
        ByteLength = byteLength;
        RelevantDate = relevantDate;
    }

    public string FullPath { get; }
    public PreviewAvailability Availability { get; }
    public string Reason { get; }
    public PreviewFrame? Frame => Volatile.Read(ref frame);
    public int? PixelWidth => Frame?.Width;
    public int? PixelHeight => Frame?.Height;
    public long? ByteLength { get; }
    public DateTimeOffset? RelevantDate { get; }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal static ReviewPreviewResult Success(
        string fullPath,
        PreviewFrame frame,
        long byteLength,
        DateTimeOffset? relevantDate) =>
        new(fullPath, PreviewAvailability.Available, string.Empty,
            frame, byteLength, relevantDate);

    public static ReviewPreviewResult Failure(
        string fullPath,
        PreviewAvailability availability,
        string reason) =>
        Failure(fullPath, availability, reason, null, null);

    internal static ReviewPreviewResult Failure(
        string fullPath,
        PreviewAvailability availability,
        string reason,
        long? byteLength,
        DateTimeOffset? relevantDate)
    {
        if (availability is PreviewAvailability.Available)
            throw new ArgumentException("A failure cannot be available.", nameof(availability));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(fullPath, availability, reason, null, byteLength, relevantDate);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref frame, null)?.Dispose();
    }
}

public sealed class PreviewDecodeResult : IDisposable
{
    private PreviewFrame? frame;

    private PreviewDecodeResult(
        string fullPath,
        PreviewAvailability availability,
        string reason,
        PreviewFrame? frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(reason);
        if ((availability is PreviewAvailability.Available) != (frame is not null))
            throw new ArgumentException("Only an available preview can contain pixels.", nameof(frame));

        FullPath = fullPath;
        Availability = availability;
        Reason = reason;
        this.frame = frame;
    }

    public string FullPath { get; }
    public PreviewAvailability Availability { get; }
    public string Reason { get; }
    public PreviewFrame? Frame => Volatile.Read(ref frame);

    internal static PreviewDecodeResult Success(string fullPath, PreviewFrame frame) =>
        new(fullPath, PreviewAvailability.Available, string.Empty, frame);

    public static PreviewDecodeResult Failure(
        string fullPath, PreviewAvailability availability, string reason)
    {
        if (availability is PreviewAvailability.Available)
            throw new ArgumentException("A failure cannot be available.", nameof(availability));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(fullPath, availability, reason, null);
    }

    public PreviewFrame TakeFrame() => Interlocked.Exchange(ref frame, null)
        ?? throw new InvalidOperationException("The preview result does not own a frame.");

    public void Dispose() => Interlocked.Exchange(ref frame, null)?.Dispose();
}

public sealed class PreviewFrame : IDisposable
{
    private IMemoryOwner<byte>? owner;

    internal PreviewFrame(int width, int height, IMemoryOwner<byte> owner)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Width = width;
        Height = height;
        ByteLength = checked(width * height * 4);
        OwnedByteLength = owner.Memory.Length;
        if (OwnedByteLength < ByteLength)
        {
            this.owner = null;
            owner.Dispose();
            throw new ArgumentException(
                "The memory owner is smaller than the frame pixels.", nameof(owner));
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int ByteLength { get; }
    public int OwnedByteLength { get; }
    public bool IsDisposed => owner is null;
    public ReadOnlyMemory<byte> DisplayPixels => Pixels;
    public ReadOnlyMemory<byte> AnalysisPixels => Pixels;

    private ReadOnlyMemory<byte> Pixels =>
        (owner ?? throw new ObjectDisposedException(nameof(PreviewFrame))).Memory[..ByteLength];

    internal static PreviewFrame Rent(int width, int height) =>
        Rent(width, height, MemoryPool<byte>.Shared);

    internal static PreviewFrame Rent(int width, int height, MemoryPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var length = checked(width * height * 4);
        return new(width, height, pool.Rent(length));
    }

    internal Memory<byte> WritablePixels =>
        (owner ?? throw new ObjectDisposedException(nameof(PreviewFrame))).Memory[..ByteLength];

    internal PreviewFrame Clone()
    {
        var clone = Rent(Width, Height);
        Pixels.CopyTo(clone.WritablePixels);
        return clone;
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref owner, null);
        if (owned is null) return;
        owned.Memory.Span.Clear();
        owned.Dispose();
    }
}
