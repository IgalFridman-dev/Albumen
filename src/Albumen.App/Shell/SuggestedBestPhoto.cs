namespace Albumen.App.Shell;

public sealed record PhotoFacts(long OccurrenceId, int PixelWidth, int PixelHeight, long? ByteCount);

public sealed record SuggestedBest(long OccurrenceId, string Reason);

// A visual hint for Look-alike and Maybe similar sets only. It never selects, keeps or
// removes anything: Selection stays manual (E3-S4 amendment, 2026-10-17).
internal static class SuggestedBestPhoto
{
    internal const int MaximumSetSize = 24;

    internal static SuggestedBest? Pick(IReadOnlyList<PhotoFacts> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count is < 2 or > MaximumSetSize) return null;

        var mostPixels = photos.Max(Pixels);
        var biggest = photos.Where(photo => Pixels(photo) == mostPixels).ToArray();
        var largestFile = UniqueLargestFile(photos);
        if (biggest.Length == 1)
            return new(biggest[0].OccurrenceId, largestFile == biggest[0].OccurrenceId
                ? "most pixels and largest file"
                : "most pixels");

        var tieBreak = UniqueLargestFile(biggest);
        return tieBreak is { } id ? new(id, "largest file at the highest pixel size") : null;
    }

    private static long Pixels(PhotoFacts photo) => (long)photo.PixelWidth * photo.PixelHeight;

    private static long? UniqueLargestFile(IReadOnlyCollection<PhotoFacts> photos)
    {
        if (photos.Any(photo => photo.ByteCount is null)) return null;
        var largest = photos.Max(photo => photo.ByteCount!.Value);
        var owners = photos.Where(photo => photo.ByteCount == largest).ToArray();
        return owners.Length == 1 ? owners[0].OccurrenceId : null;
    }
}
