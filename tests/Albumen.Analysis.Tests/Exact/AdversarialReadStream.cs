namespace Albumen.Analysis.Tests.Exact;

internal sealed class AdversarialReadStream : Stream
{
    private readonly byte[] content;
    private readonly int[] readSizes;
    private readonly long? failAtPosition;
    private int readSizeIndex;
    private long position;

    public AdversarialReadStream(
        byte[] content,
        int[]? readSizes = null,
        long? failAtPosition = null)
    {
        this.content = content;
        this.readSizes = readSizes is { Length: > 0 } ? readSizes : [int.MaxValue];
        this.failAtPosition = failAtPosition;
    }

    public int MaximumRequestedCount { get; private set; }

    public long BytesReturned { get; private set; }

    public bool WasDisposed { get; private set; }

    public override bool CanRead => !WasDisposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MaximumRequestedCount = Math.Max(MaximumRequestedCount, buffer.Length);

        if (failAtPosition is long failurePosition && position >= failurePosition)
        {
            throw new IOException("Injected read failure.");
        }

        if (position == content.LongLength)
        {
            return ValueTask.FromResult(0);
        }

        var requestedReadSize = readSizes[readSizeIndex++ % readSizes.Length];
        var available = checked((int)(content.LongLength - position));
        var count = Math.Min(buffer.Length, Math.Min(requestedReadSize, available));
        if (failAtPosition is long boundary)
        {
            count = Math.Min(count, checked((int)(boundary - position)));
        }

        content.AsMemory(checked((int)position), count).CopyTo(buffer);
        position += count;
        BytesReturned += count;
        return ValueTask.FromResult(count);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Tests require asynchronous reads.");

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class CancellationReadStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
