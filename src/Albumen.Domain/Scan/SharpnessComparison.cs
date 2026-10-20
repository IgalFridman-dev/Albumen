namespace Albumen.Domain.Scan;

// Sharpness is compared only inside one set: its photos show the same scene, so content
// cancels out and a plain ratio is enough. A set whose sharpest photo has too little fine
// detail gets no claim at all, because there the score is mostly noise. Evidence only:
// nothing here selects, keeps or removes a photo.
public static class SharpnessComparison
{
    public const double MinimumDetail = 25;
    public const double ClearMargin = 1.25;
    public const double BlurrierFactor = 4;

    // The photo clearly sharper than every other one; null when any photo is unmeasured
    // or none stands out.
    public static long? Sharpest(IReadOnlyCollection<(long OccurrenceId, double? Sharpness)> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count < 2 || photos.Any(photo => photo.Sharpness is null)) return null;
        var ranked = photos.OrderByDescending(photo => photo.Sharpness).ToArray();
        var best = ranked[0].Sharpness!.Value;
        return best >= MinimumDetail && best >= ranked[1].Sharpness!.Value * ClearMargin
            ? ranked[0].OccurrenceId
            : null;
    }

    // Photos that kept a quarter or less of the sharpest photo's fine detail.
    public static IReadOnlySet<long> Blurrier(IReadOnlyCollection<(long OccurrenceId, double? Sharpness)> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        var measured = photos.Where(photo => photo.Sharpness is not null).ToArray();
        var best = measured.Length < 2 ? 0 : measured.Max(photo => photo.Sharpness!.Value);
        if (best < MinimumDetail) return new HashSet<long>();
        return measured.Where(photo => photo.Sharpness!.Value * BlurrierFactor <= best)
            .Select(photo => photo.OccurrenceId)
            .ToHashSet();
    }
}
