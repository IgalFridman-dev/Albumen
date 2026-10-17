using System.Collections.Immutable;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

// Ephemeral, current-Scan state. No signature or group data leaves ScanStore.
internal sealed class NearDuplicateGroupIndex
{
    private readonly Dictionary<string, PublishedGroup> published = [];
    private ImmutableArray<CandidateGroupProjection> projections = [];

    private NearDuplicateGroupIndex(NearDuplicateGroupIndex source)
    {
        projections = source.projections;
        foreach (var pair in source.published) published.Add(pair.Key, pair.Value);
    }

    public NearDuplicateGroupIndex() { }

    public NearDuplicateGroupIndex Clone() => new(this);

    public void Replace(ImmutableArray<NearFeatureResult> results, ImmutableArray<ScanFileOccurrence> rows)
    {
        var successful = results
            .Where(result => result.Outcome.State == ScanStageState.Succeeded && result.Signature is not null)
            .OrderBy(result => result.Token.OccurrenceId)
            .ToArray();
        var relationships = new List<NearDuplicateEvidence>();
        for (var leftIndex = 0; leftIndex < successful.Length; leftIndex++)
        for (var rightIndex = leftIndex + 1; rightIndex < successful.Length; rightIndex++)
        {
            var left = successful[leftIndex];
            var right = successful[rightIndex];
            if (SameBytes(left.Content!, right.Content!)) continue;
            var evidence = NearDuplicateThresholds.Classify(
                left.Signature!, right.Signature!, left.Token, right.Token);
            if (evidence.Kind != NearRelationshipKind.NotRelated) relationships.Add(evidence);
        }

        var next = ImmutableArray.CreateBuilder<CandidateGroupProjection>();
        var sets = LookAlikeSets(relationships);
        foreach (var set in sets)
        {
            // A Near component that absorbed nothing keeps its Near category and Near evidence;
            // a set that joined more photos through Uncertain links is reviewed as Uncertain.
            var evidence = relationships.Where(edge => set.Members.Contains(edge.Left.OccurrenceId)
                && set.Members.Contains(edge.Right.OccurrenceId)
                && (!set.IsSingleNearComponent || edge.Kind == NearRelationshipKind.Near));
            next.Add(Project(set.Members, evidence,
                set.IsSingleNearComponent ? CandidateCategory.NearDuplicate : CandidateCategory.Uncertain, rows));
        }

        // A photo related to only part of a set is not forced into it; its relationship
        // still surfaces as an Uncertain pair so no relationship is hidden.
        foreach (var edge in relationships.Where(item => item.Kind == NearRelationshipKind.Uncertain))
        {
            if (sets.Any(set => set.Members.Contains(edge.Left.OccurrenceId)
                && set.Members.Contains(edge.Right.OccurrenceId))) continue;
            next.Add(Project([edge.Left.OccurrenceId, edge.Right.OccurrenceId], [edge],
                CandidateCategory.Uncertain, rows));
        }
        projections = next
            .OrderBy(value => value.Category)
            .ThenBy(value => value.Members[0].Token.OccurrenceId)
            .ToImmutableArray();
    }

    public ImmutableArray<CandidateGroup> Groups(ScanPin pin)
    {
        var next = new Dictionary<string, PublishedGroup>();
        var groups = ImmutableArray.CreateBuilder<CandidateGroup>();
        foreach (var projection in projections)
        {
            var key = Key(projection);
            var fingerprint = Fingerprint(projection);
            var prior = published.TryGetValue(key, out var value) && value.Pin == pin ? value : null;
            var id = prior?.GroupId ?? Guid.NewGuid();
            var revision = prior is null ? 1 : prior.Fingerprint == fingerprint
                ? prior.GroupRevision : checked(prior.GroupRevision + 1);
            var group = new CandidateGroup(id, revision, pin, projection.Category,
                projection.Members, [], projection.Uncertain, null,
                new PotentialRecovery(null, RecoveryUnavailableReason.InconsistentFacts))
            {
                Similarity = projection.Similarity,
            };
            groups.Add(group);
            next.Add(key, new(id, revision, pin, fingerprint));
        }
        published.Clear();
        foreach (var pair in next) published.Add(pair.Key, pair.Value);
        return groups.ToImmutable();
    }

    public bool IsCurrent(Guid groupId, long groupRevision) =>
        published.Values.Any(group => group.GroupId == groupId && group.GroupRevision == groupRevision);

    private static CandidateGroupProjection Project(IEnumerable<long> occurrenceIds,
        IEnumerable<NearDuplicateEvidence> evidence, CandidateCategory category,
        ImmutableArray<ScanFileOccurrence> rows)
    {
        var ids = occurrenceIds.Order().ToHashSet();
        var members = rows.Where(row => ids.Contains(row.Token.OccurrenceId))
            .OrderBy(row => row.Token.OccurrenceId).ToImmutableArray();
        return new(category, members,
            evidence.OrderBy(item => item.Left.OccurrenceId).ThenBy(item => item.Right.OccurrenceId).ToImmutableArray(),
            category == CandidateCategory.Uncertain);
    }

    // Near relationships join photos transitively, as before. Those Near components and the
    // remaining photos with Uncertain relationships are then merged with complete linkage:
    // two sets merge only when every cross pair is related, closest pair first, so loosely
    // chained photos are not pulled together and no new threshold is introduced.
    private static List<LookAlikeSet> LookAlikeSets(IReadOnlyList<NearDuplicateEvidence> relationships)
    {
        var related = new Dictionary<(long, long), NearDuplicateEvidence>();
        foreach (var edge in relationships) related[PairKey(edge.Left.OccurrenceId, edge.Right.OccurrenceId)] = edge;

        var sets = Components(relationships.Where(edge => edge.Kind == NearRelationshipKind.Near))
            .Select(component => new LookAlikeSet(component, true))
            .ToList();
        foreach (var node in relationships
            .Where(edge => edge.Kind == NearRelationshipKind.Uncertain)
            .SelectMany(edge => new[] { edge.Left.OccurrenceId, edge.Right.OccurrenceId })
            .Distinct().Order())
            if (!sets.Any(set => set.Members.Contains(node))) sets.Add(new([node], false));

        while (true)
        {
            var owner = new Dictionary<long, int>();
            for (var index = 0; index < sets.Count; index++)
                foreach (var member in sets[index].Members) owner[member] = index;

            (int Left, int Right, int Hamming, int Color, long First, long Second)? best = null;
            var tried = new HashSet<(int, int)>();
            foreach (var edge in relationships.Where(edge => edge.Kind == NearRelationshipKind.Uncertain))
            {
                var a = owner[edge.Left.OccurrenceId];
                var b = owner[edge.Right.OccurrenceId];
                if (a == b || !tried.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                var linkage = Linkage(sets[a].Members, sets[b].Members, related);
                if (linkage is null) continue;
                var first = Math.Min(sets[a].Members.Min(), sets[b].Members.Min());
                var second = Math.Max(sets[a].Members.Min(), sets[b].Members.Min());
                var candidate = (Math.Min(a, b), Math.Max(a, b), linkage.Value.Hamming, linkage.Value.Color, first, second);
                if (best is null || Precedes(candidate, best.Value)) best = candidate;
            }
            if (best is null) break;

            var merged = new HashSet<long>(sets[best.Value.Left].Members);
            merged.UnionWith(sets[best.Value.Right].Members);
            sets.RemoveAt(best.Value.Right);
            sets[best.Value.Left] = new(merged, false);
        }
        return sets.Where(set => set.Members.Count > 1).ToList();
    }

    private static (int Hamming, int Color)? Linkage(IReadOnlySet<long> left, IReadOnlySet<long> right,
        Dictionary<(long, long), NearDuplicateEvidence> related)
    {
        var hamming = 0;
        var color = 0;
        foreach (var a in left)
        foreach (var b in right)
        {
            if (!related.TryGetValue(PairKey(a, b), out var edge)) return null;
            hamming = Math.Max(hamming, edge.HammingDistance);
            color = Math.Max(color, edge.ColorDistance);
        }
        return (hamming, color);
    }

    private static bool Precedes(
        (int Left, int Right, int Hamming, int Color, long First, long Second) candidate,
        (int Left, int Right, int Hamming, int Color, long First, long Second) best) =>
        (candidate.Hamming, candidate.Color, candidate.First, candidate.Second)
            .CompareTo((best.Hamming, best.Color, best.First, best.Second)) < 0;

    private static (long, long) PairKey(long a, long b) => a < b ? (a, b) : (b, a);

    private static List<HashSet<long>> Components(IEnumerable<NearDuplicateEvidence> edges)
    {
        var remaining = edges.ToList();
        var nodes = remaining.SelectMany(edge => new[] { edge.Left.OccurrenceId, edge.Right.OccurrenceId })
            .Distinct().Order().ToArray();
        var components = new List<HashSet<long>>();
        foreach (var node in nodes)
        {
            if (components.Any(component => component.Contains(node))) continue;
            var component = new HashSet<long> { node };
            var queue = new Queue<long>();
            queue.Enqueue(node);
            while (queue.TryDequeue(out var current))
                foreach (var peer in remaining.Where(edge => edge.Left.OccurrenceId == current || edge.Right.OccurrenceId == current)
                    .Select(edge => edge.Left.OccurrenceId == current ? edge.Right.OccurrenceId : edge.Left.OccurrenceId))
                    if (component.Add(peer)) queue.Enqueue(peer);
            components.Add(component);
        }
        return components;
    }

    private static bool SameBytes(ContentRevision left, ContentRevision right) =>
        left.ByteLength == right.ByteLength && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static string Key(CandidateGroupProjection projection) =>
        $"{projection.Category}:{string.Join(",", projection.Members.Select(row => row.Token.OccurrenceId))}";

    private static string Fingerprint(CandidateGroupProjection projection) =>
        $"{Key(projection)}:{string.Join(";", projection.Similarity.Select(edge => $"{edge.Left.OccurrenceRevision}:{edge.Right.OccurrenceRevision}:{edge.HammingDistance}:{edge.ColorDistance}:{edge.Kind}"))}";

    private sealed record LookAlikeSet(HashSet<long> Members, bool IsSingleNearComponent);
    private sealed record PublishedGroup(Guid GroupId, long GroupRevision, ScanPin Pin, string Fingerprint);
    private sealed record CandidateGroupProjection(CandidateCategory Category,
        ImmutableArray<ScanFileOccurrence> Members, ImmutableArray<NearDuplicateEvidence> Similarity, bool Uncertain);
}
