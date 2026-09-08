using System.Collections.Immutable;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

// Data owned exclusively by the Scan turn. Hash buckets only propose work;
// connected components of accepted byte comparisons define actual groups.
internal sealed class ExactGroupIndex
{
    private readonly Dictionary<PhysicalFileKey, (OccurrenceWork Work, ContentRevision Content)> representatives = [];
    private readonly HashSet<ComparisonWork> pending = [];
    private readonly List<EqualityEvidence> equality = [];
    private readonly Dictionary<long, Guid> groupIds = [];

    public int PendingCount => pending.Count;

    public ContentRevision? ContentFor(PhysicalFileKey key) =>
        representatives.TryGetValue(key, out var representative) ? representative.Content : null;

    public ImmutableArray<ComparisonWork> Add(OccurrenceWork work, ContentRevision content)
    {
        if (representatives.ContainsKey(content.PhysicalFileKey)) return [];
        var comparisons = ImmutableArray.CreateBuilder<ComparisonWork>();
        var visited = new HashSet<PhysicalFileKey>();
        foreach (var representative in representatives.Values.OrderBy(value => value.Work.Token.OccurrenceId))
        {
            if (representative.Content.ByteLength != content.ByteLength || representative.Content.Sha256 != content.Sha256
                || visited.Contains(representative.Content.PhysicalFileKey)) continue;
            visited.UnionWith(Component(representative.Content.PhysicalFileKey));
            var comparison = new ComparisonWork(work, content, representative.Work, representative.Content);
            pending.Add(comparison);
            comparisons.Add(comparison);
        }
        representatives.Add(content.PhysicalFileKey, (work, content));
        return comparisons.ToImmutable();
    }

    public bool IsPending(ComparisonWork work) => pending.Contains(work);
    public bool HasPending(OccurrenceToken token) => pending.Any(work => work.Left.Token == token || work.Right.Token == token);
    public void Settle(ComparisonWork work) => pending.Remove(work);
    public void Settle(OccurrenceToken token) => pending.RemoveWhere(work => work.Left.Token == token || work.Right.Token == token);
    public void SettleAll() => pending.Clear();

    public void Accept(ComparisonWork work)
    {
        equality.Add(new EqualityEvidence(work.Left.Token, work.Right.Token, work.LeftContent, work.RightContent));
    }

    public void Remove(PhysicalFileKey key)
    {
        representatives.Remove(key);
        equality.RemoveAll(edge => edge.LeftContent.PhysicalFileKey == key || edge.RightContent.PhysicalFileKey == key);
        pending.RemoveWhere(work => work.LeftContent.PhysicalFileKey == key || work.RightContent.PhysicalFileKey == key);
    }

    public ImmutableArray<CandidateGroup> Groups(ScanPin pin, ImmutableArray<ScanFileOccurrence> rows)
    {
        var groups = ImmutableArray.CreateBuilder<CandidateGroup>();
        var visited = new HashSet<PhysicalFileKey>();
        foreach (var representative in representatives.Values.OrderBy(value => value.Work.Token.OccurrenceId))
        {
            var key = representative.Content.PhysicalFileKey;
            if (visited.Contains(key)) continue;
            var component = Component(key);
            visited.UnionWith(component);
            if (component.Count < 2) continue;
            var members = rows.Where(row => row.Exact.State == ScanStageState.Succeeded
                && row.PhysicalFileKey is { } physical && component.Contains(physical)).ToImmutableArray();
            var first = members[0].Token.OccurrenceId;
            if (!groupIds.TryGetValue(first, out var groupId)) groupIds.Add(first, groupId = Guid.NewGuid());
            groups.Add(new CandidateGroup(groupId, pin, CandidateCategory.Exact, members,
                equality.Where(edge => component.Contains(edge.LeftContent.PhysicalFileKey)
                    && component.Contains(edge.RightContent.PhysicalFileKey)).ToImmutableArray(),
                false, null, ExactAccounting.Calculate(members)));
        }
        return groups.ToImmutable();
    }

    private HashSet<PhysicalFileKey> Component(PhysicalFileKey key)
    {
        var result = new HashSet<PhysicalFileKey> { key };
        var queue = new Queue<PhysicalFileKey>();
        queue.Enqueue(key);
        while (queue.TryDequeue(out var next))
        {
            foreach (var edge in equality)
            {
                PhysicalFileKey? peer = edge.LeftContent.PhysicalFileKey == next ? edge.RightContent.PhysicalFileKey
                    : edge.RightContent.PhysicalFileKey == next ? edge.LeftContent.PhysicalFileKey : null;
                if (peer is { } value && result.Add(value)) queue.Enqueue(value);
            }
        }
        return result;
    }
}
