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
        var near = relationships.Where(item => item.Kind == NearRelationshipKind.Near).ToArray();
        var nearComponents = Components(near);
        foreach (var component in nearComponents)
            next.Add(Project(component, near.Where(edge => component.Contains(edge.Left.OccurrenceId)
                && component.Contains(edge.Right.OccurrenceId)), CandidateCategory.NearDuplicate, rows));

        foreach (var edge in relationships.Where(item => item.Kind == NearRelationshipKind.Uncertain))
        {
            if (nearComponents.Any(component => component.Contains(edge.Left.OccurrenceId)
                && component.Contains(edge.Right.OccurrenceId))) continue;
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

    private sealed record PublishedGroup(Guid GroupId, long GroupRevision, ScanPin Pin, string Fingerprint);
    private sealed record CandidateGroupProjection(CandidateCategory Category,
        ImmutableArray<ScanFileOccurrence> Members, ImmutableArray<NearDuplicateEvidence> Similarity, bool Uncertain);
}
