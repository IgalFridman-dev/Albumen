namespace PhotoSift.Infrastructure.Windows.Preview;

// Windows decodes a JPEG that ends early without an error and fills the missing part with
// gray, so a cut-off copy can look like a near match of anything gray. This walks the
// JPEG's segments and compressed data and reports whether the end-of-image marker is
// reached. Bytes after that marker (for example a motion-photo video) are ignored, and
// a stream that is not a JPEG is never reported as ending early.
internal static class JpegCompleteness
{
    private const int StartOfImage = 0xD8;
    private const int EndOfImage = 0xD9;
    private const int StartOfScan = 0xDA;

    public static bool EndsBeforeImageData(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new Reader(stream, cancellationToken);
        if (reader.Next() != 0xFF || reader.Next() != StartOfImage) return false;

        var marker = reader.NextMarker();
        while (marker >= 0)
        {
            if (marker == EndOfImage) return false;
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                marker = reader.NextMarker();
                continue;
            }

            var high = reader.Next();
            var low = reader.Next();
            if (low < 0) return true;
            var length = (high << 8) | low;
            if (length < 2 || !reader.Skip(length - 2)) return true;
            marker = marker == StartOfScan ? reader.MarkerAfterScanData() : reader.NextMarker();
        }
        return true;
    }

    private sealed class Reader(Stream stream, CancellationToken cancellationToken)
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int count;
        private int index;

        public int Next()
        {
            if (index == count && !Fill()) return -1;
            return buffer[index++];
        }

        public bool Skip(int length)
        {
            while (length > 0)
            {
                if (index == count && !Fill()) return false;
                var step = Math.Min(length, count - index);
                index += step;
                length -= step;
            }
            return true;
        }

        // Skips stray bytes up to the next 0xFF and any fill bytes after it, as decoders do.
        // Returns the marker code, or -1 when the stream ends first.
        public int NextMarker()
        {
            var code = Next();
            while (code is >= 0 and not 0xFF) code = Next();
            if (code < 0) return -1;
            code = Next();
            while (code == 0xFF) code = Next();
            return code;
        }

        // Compressed data holds 0xFF only as 0xFF00 (a data byte) or a restart marker, so the
        // first other marker ends the scan.
        public int MarkerAfterScanData()
        {
            while (true)
            {
                if (index == count && !Fill()) return -1;
                var found = Array.IndexOf(buffer, (byte)0xFF, index, count - index);
                if (found < 0)
                {
                    index = count;
                    continue;
                }
                index = found + 1;
                var code = Next();
                while (code == 0xFF) code = Next();
                if (code < 0) return -1;
                if (code != 0x00 && code is not (>= 0xD0 and <= 0xD7)) return code;
            }
        }

        private bool Fill()
        {
            cancellationToken.ThrowIfCancellationRequested();
            count = stream.Read(buffer, 0, buffer.Length);
            index = 0;
            return count > 0;
        }
    }
}
