using Albumen.Domain.Scan;

namespace Albumen.App.Shell;

public sealed record PhotoFacts(long OccurrenceId, int PixelWidth, int PixelHeight, long? ByteCount,
    double? Sharpness = null);

public sealed record SuggestedBest(long OccurrenceId, string Reason);

// A visual hint for Look-alike and Maybe similar sets only. It never selects, keeps or
// removes anything: Selection stays manual (E3-S4 amendment, 2026-10-17). A clearly
// sharpest photo comes first, then the most pixels, then the largest file.
internal static class SuggestedBestPhoto
{
    internal const int MaximumSetSize = 24;

    internal static SuggestedBest? Pick(IReadOnlyList<PhotoFacts> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count is < 2 or > MaximumSetSize) return null;

        var sharpness = photos.Select(photo => (photo.OccurrenceId, photo.Sharpness)).ToArray();
        var sharpest = SharpnessComparison.Sharpest(sharpness);
        var mostPixels = photos.Max(Pixels);
        var biggest = photos.Where(photo => Pixels(photo) == mostPixels).ToArray();
        long? uniqueMostPixels = biggest.Length == 1 ? biggest[0].OccurrenceId : null;
        var largestFile = UniqueLargestFile(photos);

        SuggestedBest? suggestion = null;
        if ((sharpest ?? uniqueMostPixels) is { } pick)
        {
            List<string> reasons = [];
            if (pick == sharpest) reasons.Add("sharpest");
            if (pick == uniqueMostPixels) reasons.Add("most pixels");
            if (pick == largestFile) reasons.Add("largest file");
            suggestion = new(pick, Join(reasons));
        }
        else if (UniqueLargestFile(biggest) is { } tieBreak)
        {
            suggestion = new(tieBreak, "largest file at the highest pixel size");
        }

        // Never suggest a photo the same set's cards call blurrier.
        return suggestion is not null && SharpnessComparison.Blurrier(sharpness).Contains(suggestion.OccurrenceId)
            ? null
            : suggestion;
    }

    private static long Pixels(PhotoFacts photo) => (long)photo.PixelWidth * photo.PixelHeight;

    private static long? UniqueLargestFile(IReadOnlyCollection<PhotoFacts> photos)
    {
        if (photos.Any(photo => photo.ByteCount is null)) return null;
        var largest = photos.Max(photo => photo.ByteCount!.Value);
        var owners = photos.Where(photo => photo.ByteCount == largest).ToArray();
        return owners.Length == 1 ? owners[0].OccurrenceId : null;
    }

    private static string Join(List<string> reasons) => reasons.Count == 1
        ? reasons[0]
        : $"{string.Join(", ", reasons[..^1])} and {reasons[^1]}";
}
