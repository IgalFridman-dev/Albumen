using PhotoSift.Infrastructure.Windows.Preview;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Preview;

public sealed class JpegCompletenessTests
{
    private static readonly byte[] Embedded =
        [0xFF, 0xD8, .. Segment(0xC0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]), .. Scan([0x12, 0x34]), 0xFF, 0xD9];

    // Two scans with tables between them, as in a progressive JPEG. The compressed data
    // holds stuffed 0xFF00 bytes, restart markers and fill bytes before a restart marker.
    private static readonly byte[] Complete =
    [
        0xFF, 0xD8,
        .. Segment(0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0]),
        .. Segment(0xE1, [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. Embedded]),
        .. Segment(0xEC, [0xFF, 0xD9, 0xFF, 0x00, 0xFF, 0xDA]),
        .. Segment(0xDB, [0, 1, 2, 3]),
        .. Segment(0xC2, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]),
        .. Segment(0xC4, [0, 1, 2, 3]),
        .. Scan([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0xFF, 0xD1, 0x78]),
        0xFF, 0xFF,
        .. Segment(0xC4, [4, 5, 6, 7]),
        .. Scan([0x9A, 0xFF, 0x00, 0xBC]),
        0xFF, 0xD9,
    ];

    public static TheoryData<string, byte[]> CompleteFiles => new()
    {
        { "segments, two scans and an embedded thumbnail", Complete },
        { "data after the end marker", [.. Complete, 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0xFF, 0xDA] },
        { "a second picture after the first", [.. Complete, .. Embedded] },
        { "not a JPEG", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A] },
        { "empty", [] },
    };

    [Theory]
    [MemberData(nameof(CompleteFiles))]
    public void CompleteFilesAndOtherFormatsAreNotReportedAsEndingEarly(string name, byte[] bytes)
    {
        Assert.False(EndsEarly(bytes, oneByteAtATime: false), name);
        Assert.False(EndsEarly(bytes, oneByteAtATime: true), name);
    }

    [Fact]
    public void EveryCutBeforeTheEndMarkerIsReportedAsEndingEarly()
    {
        for (var length = 2; length < Complete.Length; length++)
        {
            Assert.True(EndsEarly(Complete[..length], oneByteAtATime: false), $"Cut at {length}.");
            Assert.True(EndsEarly(Complete[..length], oneByteAtATime: true), $"Cut at {length}.");
        }
    }

    [Fact]
    public void ACanceledCheckStops()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var stream = new MemoryStream(Complete);

        Assert.Throws<OperationCanceledException>(() =>
            JpegCompleteness.EndsBeforeImageData(stream, canceled.Token));
    }

    private static bool EndsEarly(byte[] bytes, bool oneByteAtATime)
    {
        using var stream = oneByteAtATime ? new OneByteAtATimeStream(bytes) : new MemoryStream(bytes);
        return JpegCompleteness.EndsBeforeImageData(stream, TestContext.Current.CancellationToken);
    }

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    private static byte[] Scan(byte[] data) => [.. Segment(0xDA, [1, 1, 0, 0, 63, 0]), .. data];

    private sealed class OneByteAtATimeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, 1));
    }
}
