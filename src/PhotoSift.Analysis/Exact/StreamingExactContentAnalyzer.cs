using System.Buffers;
using System.Security.Cryptography;
using PhotoSift.Application.Scan;

namespace PhotoSift.Analysis.Exact;

public sealed class StreamingExactContentAnalyzer : IExactContentAnalyzer
{
    public const int BufferSize = 65536;

    public async ValueTask<StreamDigest> HashAsync(
        Stream content,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = await HashExpectedBytesAsync(
                content,
                expectedLength,
                buffer,
                hash,
                cancellationToken).ConfigureAwait(false);
            await EnsureEndOfStreamAsync(content, buffer, cancellationToken).ConfigureAwait(false);

            return new StreamDigest(total, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public async ValueTask<StreamEquality> CompareAsync(
        Stream left,
        Stream right,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);

        var leftBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[]? rightBuffer = null;
        try
        {
            rightBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using var leftHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var rightHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = expectedLength;
            var total = 0L;
            var equal = true;

            while (remaining > 0)
            {
                var requested = checked((int)Math.Min(BufferSize, remaining));
                var leftRead = await FillAsync(
                    left,
                    leftBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                var rightRead = await FillAsync(
                    right,
                    rightBuffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);

                EnsureExpectedRead(leftRead, requested);
                EnsureExpectedRead(rightRead, requested);

                leftHash.AppendData(leftBuffer.AsSpan(0, leftRead));
                rightHash.AppendData(rightBuffer.AsSpan(0, rightRead));
                equal &= leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead));
                total = checked(total + leftRead);
                remaining -= leftRead;
            }

            var leftHasExtraData = await HasExtraDataAsync(
                left,
                leftBuffer,
                cancellationToken).ConfigureAwait(false);
            var rightHasExtraData = await HasExtraDataAsync(
                right,
                rightBuffer,
                cancellationToken).ConfigureAwait(false);
            if (leftHasExtraData || rightHasExtraData)
            {
                throw new IOException("Stream contains more bytes than the expected length.");
            }

            var leftDigest = new StreamDigest(total, Convert.ToHexString(leftHash.GetHashAndReset()));
            var rightDigest = new StreamDigest(total, Convert.ToHexString(rightHash.GetHashAndReset()));
            return new StreamEquality(equal, leftDigest, rightDigest);
        }
        finally
        {
            if (rightBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(rightBuffer, clearArray: true);
            }

            ArrayPool<byte>.Shared.Return(leftBuffer, clearArray: true);
        }
    }

    private static async ValueTask<long> HashExpectedBytesAsync(
        Stream content,
        long expectedLength,
        byte[] buffer,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        var remaining = expectedLength;
        var total = 0L;
        while (remaining > 0)
        {
            var requested = checked((int)Math.Min(BufferSize, remaining));
            var read = await FillAsync(
                content,
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            EnsureExpectedRead(read, requested);

            hash.AppendData(buffer.AsSpan(0, read));
            total = checked(total + read);
            remaining -= read;
        }

        return total;
    }

    private static async ValueTask<int> FillAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled = checked(filled + read);
        }

        return filled;
    }

    private static void EnsureExpectedRead(int actual, int expected)
    {
        if (actual != expected)
        {
            throw new IOException("Stream ended before the expected length.");
        }
    }

    private static async ValueTask EnsureEndOfStreamAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (await HasExtraDataAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("Stream contains more bytes than the expected length.");
        }
    }

    private static async ValueTask<bool> HasExtraDataAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken) =>
        await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0;
}
