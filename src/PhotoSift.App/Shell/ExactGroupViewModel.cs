using System.Collections.Immutable;
using System.Globalization;
using PhotoSift.Domain.Scan;

namespace PhotoSift.App.Shell;

public sealed class ExactGroupViewModel
{
    public ExactGroupViewModel(CandidateGroup group)
    {
        var seen = new HashSet<PhysicalFileKey>();
        Members = group.Members.OrderBy(member => member.Token.OccurrenceId)
            .Select(member => new ScanOccurrenceViewModel(member,
                member.PhysicalFileKey is { } key && !seen.Add(key))).ToImmutableArray();
        PhysicalFileCount = seen.Count;
        AliasCount = Members.Length - PhysicalFileCount;
        Summary = $"Exact duplicate group: {PhysicalFileCount} physical files, {Members.Length} paths, {AliasCount} aliases";
        PotentialRecovery = group.PotentialRecovery.Bytes is { } bytes
            ? $"Potential recovery: up to {bytes.ToString(CultureInfo.InvariantCulture)} bytes; no removal plan selected."
            : $"Potential recovery: unavailable ({group.PotentialRecovery.Reason}).";
    }

    public int PhysicalFileCount { get; }
    public int AliasCount { get; }
    public string Summary { get; }
    public string PotentialRecovery { get; }
    public ImmutableArray<ScanOccurrenceViewModel> Members { get; }
}
