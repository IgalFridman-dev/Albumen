using System.Buffers;

namespace PhotoSift.Infrastructure.Windows.Preview;

public enum PreviewAvailability
{
    Available,
    Unsupported,
    CouldNotAnalyze,
    Unavailable,
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

    private PreviewFrame(int width, int height, IMemoryOwner<byte> owner)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Width = width;
        Height = height;
        ByteLength = checked(width * height * 4);
    }

    public int Width { get; }
    public int Height { get; }
    public int ByteLength { get; }
    public bool IsDisposed => owner is null;
    public ReadOnlyMemory<byte> DisplayPixels => Pixels;
    public ReadOnlyMemory<byte> AnalysisPixels => Pixels;

    private ReadOnlyMemory<byte> Pixels =>
        (owner ?? throw new ObjectDisposedException(nameof(PreviewFrame))).Memory[..ByteLength];

    internal static PreviewFrame Rent(int width, int height)
    {
        var length = checked(width * height * 4);
        return new(width, height, MemoryPool<byte>.Shared.Rent(length));
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
