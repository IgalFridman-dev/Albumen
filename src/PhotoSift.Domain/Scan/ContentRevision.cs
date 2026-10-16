namespace PhotoSift.Domain.Scan;

public sealed record ContentRevision
{
    public ContentRevision(PhysicalFileKey physicalFileKey, long byteLength, string sha256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        ArgumentNullException.ThrowIfNull(sha256);

        if (sha256.Length != 64 || !sha256.All(IsHexadecimalDigit))
        {
            throw new ArgumentException(
                "SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(sha256));
        }

        PhysicalFileKey = physicalFileKey;
        ByteLength = byteLength;
        Sha256 = sha256.ToUpperInvariant();
    }

    public PhysicalFileKey PhysicalFileKey { get; }

    public long ByteLength { get; }

    public string Sha256 { get; }

    private static bool IsHexadecimalDigit(char value) =>
        value is >= '0' and <= '9'
            or >= 'A' and <= 'F'
            or >= 'a' and <= 'f';
}
