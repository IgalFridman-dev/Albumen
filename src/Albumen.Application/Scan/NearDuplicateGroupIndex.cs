using System.Collections.Immutable;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

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
        var setOf = new Dictionary<long, int>();
        for (var index = 0; index < sets.Count; index++)
            foreach (var member in sets[index].Members) setOf[member] = index;
        var setEvidence = sets.Select(_ => new List<NearDuplicateEvidence>()).ToArray();
        var leftover = new List<NearDuplicateEvidence>();
        foreach (var edge in relationships)
        {
            if (setOf.TryGetValue(edge.Left.OccurrenceId, out var left)
                && setOf.TryGetValue(edge.Right.OccurrenceId, out var right) && left == right)
            {
                // A Near component that absorbed nothing keeps its Near category and Near
                // evidence; a set that joined more photos through Uncertain links is
                // reviewed as Uncertain with all of its internal evidence.
                if (!sets[left].IsSingleNearComponent || edge.Kind == NearRelationshipKind.Near)
                    setEvidence[left].Add(edge);
            }
            else if (edge.Kind == NearRelationshipKind.Uncertain)
            {
                // A photo related to only part of a set is not forced into it; that
                // relationship still surfaces as an Uncertain pair.
                leftover.Add(edge);
            }
        }
        for (var index = 0; index < sets.Count; index++)
            next.Add(Project(sets[index].Members, setEvidence[index],
                sets[index].IsSingleNearComponent ? CandidateCategory.NearDuplicate : CandidateCategory.Uncertain, rows));
        foreach (var edge in leftover)
            next.Add(Project([edge.Left.OccurrenceId, edge.Right.OccurrenceId], [edge],
                CandidateCategory.Uncertain, rows));
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
    // two sets merge only when every cross pair is related, closest pair first (largest
    // Hamming distance, then largest color distance, then lowest photo ids), so loosely
    // chained photos are not pulled together and no new threshold is introduced. Linkage
    // after a merge is the larger of the two parts' linkages (Lance-Williams), so each merge
    // only revisits the merged set's neighbors.
    private static List<LookAlikeSet> LookAlikeSets(IReadOnlyList<NearDuplicateEvidence> relationships)
    {
        var clusters = new List<Cluster>();
        var owner = new Dictionary<long, int>();
        void Add(HashSet<long> members, bool nearComponent)
        {
            foreach (var member in members) owner[member] = clusters.Count;
            clusters.Add(new(members, nearComponent));
        }

        foreach (var component in Components(relationships.Where(edge => edge.Kind == NearRelationshipKind.Near)))
            Add(component, true);
        foreach (var node in relationships
            .Where(edge => edge.Kind == NearRelationshipKind.Uncertain)
            .SelectMany(edge => new[] { edge.Left.OccurrenceId, edge.Right.OccurrenceId })
            .Distinct().Order())
            if (!owner.ContainsKey(node)) Add([node], false);

        foreach (var edge in relationships)
        {
            var a = owner[edge.Left.OccurrenceId];
            var b = owner[edge.Right.OccurrenceId];
            if (a == b) continue;
            var link = clusters[a].Links.TryGetValue(b, out var existing) ? existing : new Link(0, 0, 0);
            link = new(link.Count + 1, Math.Max(link.Hamming, edge.HammingDistance),
                Math.Max(link.Color, edge.ColorDistance));
            clusters[a].Links[b] = link;
            clusters[b].Links[a] = link;
        }

        var queue = new PriorityQueue<(int Left, int Right), (int Hamming, int Color, long First, long Second)>();
        void Offer(int a, int b, Link link)
        {
            if (link.Count != (long)clusters[a].Members.Count * clusters[b].Members.Count) return;
            var first = Math.Min(clusters[a].Lowest, clusters[b].Lowest);
            var second = Math.Max(clusters[a].Lowest, clusters[b].Lowest);
            queue.Enqueue((a, b), (link.Hamming, link.Color, first, second));
        }

        for (var a = 0; a < clusters.Count; a++)
            foreach (var (b, link) in clusters[a].Links)
                if (a < b) Offer(a, b, link);

        while (queue.TryDequeue(out var pair, out _))
        {
            var left = clusters[pair.Left];
            var right = clusters[pair.Right];
            if (!left.Alive || !right.Alive) continue;
            left.Alive = false;
            right.Alive = false;
            var members = new HashSet<long>(left.Members);
            members.UnionWith(right.Members);
            var merged = clusters.Count;
            clusters.Add(new(members, false));
            foreach (var neighbor in left.Links.Keys.Union(right.Links.Keys).Order())
            {
                if (neighbor == pair.Left || neighbor == pair.Right || !clusters[neighbor].Alive) continue;
                left.Links.TryGetValue(neighbor, out var fromLeft);
                right.Links.TryGetValue(neighbor, out var fromRight);
                var link = new Link(
                    (fromLeft?.Count ?? 0) + (fromRight?.Count ?? 0),
                    Math.Max(fromLeft?.Hamming ?? 0, fromRight?.Hamming ?? 0),
                    Math.Max(fromLeft?.Color ?? 0, fromRight?.Color ?? 0));
                clusters[neighbor].Links.Remove(pair.Left);
                clusters[neighbor].Links.Remove(pair.Right);
                clusters[neighbor].Links[merged] = link;
                clusters[merged].Links[neighbor] = link;
                Offer(merged, neighbor, link);
            }
        }

        return clusters
            .Where(cluster => cluster.Alive && cluster.Members.Count > 1)
            .Select(cluster => new LookAlikeSet(cluster.Members, cluster.IsNearComponent))
            .ToList();
    }

    private static List<HashSet<long>> Components(IEnumerable<NearDuplicateEvidence> edges)
    {
        var peers = new Dictionary<long, List<long>>();
        foreach (var edge in edges)
        {
            if (!peers.TryGetValue(edge.Left.OccurrenceId, out var left)) peers[edge.Left.OccurrenceId] = left = [];
            if (!peers.TryGetValue(edge.Right.OccurrenceId, out var right)) peers[edge.Right.OccurrenceId] = right = [];
            left.Add(edge.Right.OccurrenceId);
            right.Add(edge.Left.OccurrenceId);
        }
        var seen = new HashSet<long>();
        var components = new List<HashSet<long>>();
        foreach (var node in peers.Keys.Order())
        {
            if (!seen.Add(node)) continue;
            var component = new HashSet<long> { node };
            var queue = new Queue<long>();
            queue.Enqueue(node);
            while (queue.TryDequeue(out var current))
                foreach (var peer in peers[current])
                    if (seen.Add(peer))
                    {
                        component.Add(peer);
                        queue.Enqueue(peer);
                    }
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

    private sealed record Link(long Count, int Hamming, int Color);

    private sealed class Cluster(HashSet<long> members, bool isNearComponent)
    {
        public HashSet<long> Members { get; } = members;
        public bool IsNearComponent { get; } = isNearComponent;
        public long Lowest { get; } = members.Min();
        public Dictionary<int, Link> Links { get; } = [];
        public bool Alive { get; set; } = true;
    }
    private sealed record PublishedGroup(Guid GroupId, long GroupRevision, ScanPin Pin, string Fingerprint);
    private sealed record CandidateGroupProjection(CandidateCategory Category,
        ImmutableArray<ScanFileOccurrence> Members, ImmutableArray<NearDuplicateEvidence> Similarity, bool Uncertain);
}
