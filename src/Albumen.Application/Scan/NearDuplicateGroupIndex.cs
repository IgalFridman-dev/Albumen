using System.Collections.Immutable;
using System.Globalization;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

// Ephemeral, current-Scan state. No signature or group data leaves ScanStore.
internal sealed class NearDuplicateGroupIndex
{
    private readonly Dictionary<string, PublishedGroup> published = [];
    // The analyzed photos and their hash relationships, kept so model matches reuse them.
    private ImmutableArray<NearFeatureResult> successful = [];
    private ImmutableArray<NearDuplicateEvidence> relationships = [];
    private ImmutableArray<EmbeddingMatch> matches = [];
    private ImmutableArray<CandidateGroupProjection> projections = [];

    private NearDuplicateGroupIndex(NearDuplicateGroupIndex source)
    {
        successful = source.successful;
        relationships = source.relationships;
        matches = source.matches;
        projections = source.projections;
        foreach (var pair in source.published) published.Add(pair.Key, pair.Value);
    }

    public NearDuplicateGroupIndex() { }

    public NearDuplicateGroupIndex Clone() => new(this);

    public void Replace(ImmutableArray<NearFeatureResult> results, ImmutableArray<ScanFileOccurrence> rows)
    {
        successful = results
            .Where(result => result.Outcome.State == ScanStageState.Succeeded && result.Signature is not null)
            .OrderBy(result => result.Token.OccurrenceId)
            .ToImmutableArray();
        var related = ImmutableArray.CreateBuilder<NearDuplicateEvidence>();
        for (var leftIndex = 0; leftIndex < successful.Length; leftIndex++)
        for (var rightIndex = leftIndex + 1; rightIndex < successful.Length; rightIndex++)
        {
            var left = successful[leftIndex];
            var right = successful[rightIndex];
            if (SameBytes(left.Content!, right.Content!)) continue;
            var evidence = NearDuplicateThresholds.Classify(
                left.Signature!, right.Signature!, left.Token, right.Token);
            if (evidence.Kind != NearRelationshipKind.NotRelated) related.Add(evidence);
        }
        relationships = related.ToImmutable();
        matches = [];
        Build(rows);
    }

    /// <summary>Adds the photo model's Maybe-similar matches to the hash relationships (E4-S6).</summary>
    public void ReplaceMatches(ImmutableArray<EmbeddingMatch> matches, ImmutableArray<ScanFileOccurrence> rows)
    {
        this.matches = matches;
        Build(rows);
    }

    private void Build(ImmutableArray<ScanFileOccurrence> rows)
    {
        // Look-alike sets come from the hash alone, so they are the same with or without the model.
        // The model's matches only add Maybe-similar sets and pairs. Its links merge after every hash
        // link, so each hash merge happens exactly as without it; a Look-alike set that then takes in
        // a model match appears again inside that Maybe-similar set, and stays Look-alike on its own.
        var hash = Projections(relationships, rows);
        var model = ModelRelationships();
        var all = model.Count == 0 ? hash : Projections([.. relationships, .. model], rows);
        projections = hash.Where(value => value.Category == CandidateCategory.NearDuplicate)
            .Concat(all.Where(value => value.Category != CandidateCategory.NearDuplicate))
            .OrderBy(value => value.Category)
            .ThenBy(value => value.Members[0].Token.OccurrenceId)
            .ToImmutableArray();
    }

    // A model match relates a pair only where the hash does not: a pair the hash calls Near or
    // Uncertain keeps the hash's evidence.
    private List<NearDuplicateEvidence> ModelRelationships()
    {
        var model = new List<NearDuplicateEvidence>();
        if (matches.IsDefaultOrEmpty) return model;
        var byId = successful.ToDictionary(result => result.Token.OccurrenceId);
        foreach (var match in matches.DistinctBy(match => (match.Left, match.Right))
            .OrderBy(match => match.Left).ThenBy(match => match.Right))
        {
            if (match.Left >= match.Right || !MaybeSimilarThresholds.IsMatch(match.Cosine)
                || !byId.TryGetValue(match.Left, out var left) || !byId.TryGetValue(match.Right, out var right)
                || SameBytes(left.Content!, right.Content!)) continue;
            var evidence = NearDuplicateThresholds.Classify(
                left.Signature!, right.Signature!, left.Token, right.Token);
            if (evidence.Kind == NearRelationshipKind.NotRelated)
                model.Add(evidence with { Kind = NearRelationshipKind.Uncertain, Cosine = match.Cosine });
        }
        return model;
    }

    private static List<CandidateGroupProjection> Projections(IReadOnlyList<NearDuplicateEvidence> relationships,
        ImmutableArray<ScanFileOccurrence> rows)
    {
        var next = new List<CandidateGroupProjection>();
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
            else if (edge.Kind == NearRelationshipKind.Uncertain
                && (edge.Cosine is null || !setOf.ContainsKey(edge.Left.OccurrenceId)
                    || !setOf.ContainsKey(edge.Right.OccurrenceId)))
            {
                // A photo related to only part of a set is not forced into it; that
                // relationship still surfaces as an Uncertain pair. A model match between two
                // photos already in sets is not shown again: in a large burst that would be
                // hundreds of pairs.
                leftover.Add(edge);
            }
        }
        for (var index = 0; index < sets.Count; index++)
            next.Add(Project(sets[index].Members, setEvidence[index],
                sets[index].IsSingleNearComponent ? CandidateCategory.NearDuplicate : CandidateCategory.Uncertain, rows));
        foreach (var edge in leftover)
            next.Add(Project([edge.Left.OccurrenceId, edge.Right.OccurrenceId], [edge],
                CandidateCategory.Uncertain, rows));
        return next;
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
    // only revisits the merged set's neighbors. A link with a model match comes after every
    // hash-only link, closest weakest cosine first, and never builds a set larger than
    // MaybeSimilarThresholds.MaximumSetSize.
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
            var link = clusters[a].Links.TryGetValue(b, out var existing) ? existing : Link.None;
            link = link.Merge(new(1, edge.HammingDistance, edge.ColorDistance, edge.Cosine is not null,
                edge.Cosine ?? 1f));
            clusters[a].Links[b] = link;
            clusters[b].Links[a] = link;
        }

        var queue = new PriorityQueue<(int Left, int Right),
            (bool Model, float Distance, int Hamming, int Color, long First, long Second)>();
        void Offer(int a, int b, Link link)
        {
            if (link.Count != (long)clusters[a].Members.Count * clusters[b].Members.Count) return;
            if (link.Model && clusters[a].Members.Count + clusters[b].Members.Count > MaybeSimilarThresholds.MaximumSetSize)
                return;
            var first = Math.Min(clusters[a].Lowest, clusters[b].Lowest);
            var second = Math.Max(clusters[a].Lowest, clusters[b].Lowest);
            queue.Enqueue((a, b), (link.Model, 1f - link.Cosine, link.Hamming, link.Color, first, second));
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
                var fromLeft = left.Links.GetValueOrDefault(neighbor, Link.None);
                var fromRight = right.Links.GetValueOrDefault(neighbor, Link.None);
                var link = fromLeft.Merge(fromRight);
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
        $"{Key(projection)}:{string.Join(";", projection.Similarity.Select(edge => $"{edge.Left.OccurrenceRevision}:{edge.Right.OccurrenceRevision}:{edge.HammingDistance}:{edge.ColorDistance}:{edge.Kind}{(edge.Cosine is { } cosine ? ":" + cosine.ToString("R", CultureInfo.InvariantCulture) : "")}"))}";

    private sealed record LookAlikeSet(HashSet<long> Members, bool IsSingleNearComponent);

    // Cosine is the weakest model match in the link, 1 when it has none.
    private sealed record Link(long Count, int Hamming, int Color, bool Model, float Cosine)
    {
        public static Link None { get; } = new(0, 0, 0, false, 1f);

        public Link Merge(Link other) => new(Count + other.Count, Math.Max(Hamming, other.Hamming),
            Math.Max(Color, other.Color), Model || other.Model, Math.Min(Cosine, other.Cosine));
    }

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
