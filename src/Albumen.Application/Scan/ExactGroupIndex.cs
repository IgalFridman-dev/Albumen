using System.Collections.Immutable;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

// Data owned exclusively by the Scan turn. Hash buckets only propose work;
// connected components of accepted byte comparisons define actual groups.
internal sealed class ExactGroupIndex
{
    private readonly Func<long, long> advanceRevision;
    private readonly Dictionary<PhysicalFileKey, (OccurrenceWork Work, ContentRevision Content)> representatives = [];
    private readonly HashSet<ComparisonWork> pending = [];
    private readonly List<EqualityEvidence> equality = [];
    private readonly Dictionary<Guid, PublishedGroup> published = [];

    public ExactGroupIndex() : this(AdvanceRevision) { }

    internal ExactGroupIndex(Func<long, long> advanceRevision)
    {
        ArgumentNullException.ThrowIfNull(advanceRevision);
        this.advanceRevision = advanceRevision;
    }

    private ExactGroupIndex(ExactGroupIndex source)
    {
        advanceRevision = source.advanceRevision;
        foreach (var pair in source.representatives) representatives.Add(pair.Key, pair.Value);
        pending.UnionWith(source.pending);
        equality.AddRange(source.equality);
        foreach (var pair in source.published)
        {
            var value = pair.Value;
            published.Add(pair.Key, value with
            {
                PhysicalFiles = new HashSet<PhysicalFileKey>(value.PhysicalFiles),
            });
        }
    }

    internal ExactGroupIndex Clone() => new(this);

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
        var projections = ImmutableArray.CreateBuilder<CandidateGroupProjection>();
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
            projections.Add(new CandidateGroupProjection(
                component.ToImmutableArray(), CandidateCategory.Exact, members,
                equality.Where(edge => component.Contains(edge.LeftContent.PhysicalFileKey)
                    && component.Contains(edge.RightContent.PhysicalFileKey)).ToImmutableArray(),
                false, null, ExactAccounting.Calculate(members)));
        }
        return Publish(pin, projections.ToImmutable());
    }

    internal ImmutableArray<CandidateGroup> Publish(
        ScanPin pin,
        ImmutableArray<CandidateGroupProjection> projections)
    {
        var previous = published.Values.ToArray();
        var components = projections
            .Select(projection => projection.PhysicalFiles.ToHashSet())
            .ToArray();
        var previousMatchCounts = previous.ToDictionary(
            group => group.GroupId,
            group => components.Count(component => group.Pin == pin && component.Overlaps(group.PhysicalFiles)));
        var next = new Dictionary<Guid, PublishedGroup>();
        var groups = ImmutableArray.CreateBuilder<CandidateGroup>(projections.Length);

        for (var index = 0; index < projections.Length; index++)
        {
            var projection = projections[index];
            var component = components[index];
            var matches = previous.Where(group => group.Pin == pin && component.Overlaps(group.PhysicalFiles)).ToArray();
            var fingerprint = GroupFingerprint.Create(projection);
            var continuing = matches.Length == 1 && previousMatchCounts[matches[0].GroupId] == 1
                ? matches[0]
                : null;
            var groupId = continuing?.GroupId ?? Guid.NewGuid();
            var revision = continuing is null
                ? 1
                : continuing.Fingerprint.Equals(fingerprint)
                    ? continuing.GroupRevision
                    : advanceRevision(continuing.GroupRevision);

            groups.Add(new CandidateGroup(
                groupId,
                revision,
                pin,
                projection.Category,
                projection.Members,
                projection.Equality,
                projection.Uncertain,
                projection.KeeperOccurrenceId,
                projection.PotentialRecovery));
            next.Add(groupId, new PublishedGroup(groupId, revision, pin, component, fingerprint));
        }

        published.Clear();
        foreach (var group in next) published.Add(group.Key, group.Value);
        return groups.ToImmutable();
    }

    internal bool IsCurrent(Guid groupId, long groupRevision) =>
        published.TryGetValue(groupId, out var group) && group.GroupRevision == groupRevision;

    internal static long AdvanceRevision(long revision) => checked(revision + 1);

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

    private sealed record PublishedGroup(
        Guid GroupId,
        long GroupRevision,
        ScanPin Pin,
        HashSet<PhysicalFileKey> PhysicalFiles,
        GroupFingerprint Fingerprint);

    private sealed class GroupFingerprint : IEquatable<GroupFingerprint>
    {
        private GroupFingerprint(
            CandidateCategory category,
            ImmutableArray<MemberFingerprint> members,
            ImmutableArray<EdgeFingerprint> equality,
            bool uncertain,
            long? keeperOccurrenceId,
            PotentialRecovery potentialRecovery)
        {
            Category = category;
            Members = members;
            Equality = equality;
            Uncertain = uncertain;
            KeeperOccurrenceId = keeperOccurrenceId;
            PotentialRecovery = potentialRecovery;
        }

        private CandidateCategory Category { get; }
        private ImmutableArray<MemberFingerprint> Members { get; }
        private ImmutableArray<EdgeFingerprint> Equality { get; }
        private bool Uncertain { get; }
        private long? KeeperOccurrenceId { get; }
        private PotentialRecovery PotentialRecovery { get; }

        public static GroupFingerprint Create(CandidateGroupProjection projection)
        {
            var members = projection.Members
                .Select(row => new MemberFingerprint(
                    row.Token,
                    row.PhysicalFileKey,
                    row.Enumerate,
                    row.Exact,
                    row.Content,
                    row.Allocation))
                .OrderBy(member => member, MemberComparer.Instance)
                .ToImmutableArray();
            var equality = projection.Equality
                .Select(EdgeFingerprint.Create)
                .OrderBy(edge => edge, EdgeComparer.Instance)
                .ToImmutableArray();
            return new GroupFingerprint(
                projection.Category,
                members,
                equality,
                projection.Uncertain,
                projection.KeeperOccurrenceId,
                projection.PotentialRecovery);
        }

        public bool Equals(GroupFingerprint? other) => other is not null
            && Category == other.Category
            && Members.SequenceEqual(other.Members)
            && Equality.SequenceEqual(other.Equality)
            && Uncertain == other.Uncertain
            && KeeperOccurrenceId == other.KeeperOccurrenceId
            && PotentialRecovery == other.PotentialRecovery;

        public override bool Equals(object? obj) => obj is GroupFingerprint other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Category);
            foreach (var member in Members) hash.Add(member);
            foreach (var edge in Equality) hash.Add(edge);
            hash.Add(Uncertain);
            hash.Add(KeeperOccurrenceId);
            hash.Add(PotentialRecovery);
            return hash.ToHashCode();
        }
    }

    private readonly record struct MemberFingerprint(
        OccurrenceToken Token,
        PhysicalFileKey? PhysicalFileKey,
        StageOutcome Enumerate,
        StageOutcome Exact,
        ContentRevision? Content,
        AllocationEvidence Allocation);

    private readonly record struct EdgeFingerprint(EndpointFingerprint First, EndpointFingerprint Second)
    {
        public static EdgeFingerprint Create(EqualityEvidence evidence)
        {
            var left = new EndpointFingerprint(evidence.Left, evidence.LeftContent);
            var right = new EndpointFingerprint(evidence.Right, evidence.RightContent);
            return EndpointComparer.Instance.Compare(left, right) <= 0
                ? new EdgeFingerprint(left, right)
                : new EdgeFingerprint(right, left);
        }
    }

    private readonly record struct EndpointFingerprint(OccurrenceToken Token, ContentRevision Content);

    private sealed class MemberComparer : IComparer<MemberFingerprint>
    {
        public static MemberComparer Instance { get; } = new();

        public int Compare(MemberFingerprint left, MemberFingerprint right)
        {
            var result = CompareToken(left.Token, right.Token);
            if (result != 0) return result;
            result = ComparePhysical(left.PhysicalFileKey, right.PhysicalFileKey);
            if (result != 0) return result;
            return CompareContent(left.Content, right.Content);
        }
    }

    private sealed class EndpointComparer : IComparer<EndpointFingerprint>
    {
        public static EndpointComparer Instance { get; } = new();

        public int Compare(EndpointFingerprint left, EndpointFingerprint right)
        {
            var result = CompareToken(left.Token, right.Token);
            return result != 0 ? result : CompareContent(left.Content, right.Content);
        }
    }

    private sealed class EdgeComparer : IComparer<EdgeFingerprint>
    {
        public static EdgeComparer Instance { get; } = new();

        public int Compare(EdgeFingerprint left, EdgeFingerprint right)
        {
            var result = EndpointComparer.Instance.Compare(left.First, right.First);
            return result != 0 ? result : EndpointComparer.Instance.Compare(left.Second, right.Second);
        }
    }

    private static int CompareToken(OccurrenceToken left, OccurrenceToken right)
    {
        var result = left.Pin.ScanId.CompareTo(right.Pin.ScanId);
        if (result != 0) return result;
        result = left.Pin.ScopeRevision.CompareTo(right.Pin.ScopeRevision);
        if (result != 0) return result;
        result = left.Pin.RuleSetRevision.CompareTo(right.Pin.RuleSetRevision);
        if (result != 0) return result;
        result = left.Pin.AnalysisRevision.CompareTo(right.Pin.AnalysisRevision);
        if (result != 0) return result;
        result = left.OccurrenceId.CompareTo(right.OccurrenceId);
        if (result != 0) return result;
        result = left.OccurrenceRevision.CompareTo(right.OccurrenceRevision);
        return result != 0 ? result : left.Attempt.CompareTo(right.Attempt);
    }

    private static int ComparePhysical(PhysicalFileKey? left, PhysicalFileKey? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        var result = left.Value.VolumeSerial.CompareTo(right.Value.VolumeSerial);
        if (result != 0) return result;
        result = left.Value.FileId.High.CompareTo(right.Value.FileId.High);
        return result != 0 ? result : left.Value.FileId.Low.CompareTo(right.Value.FileId.Low);
    }

    private static int CompareContent(ContentRevision? left, ContentRevision? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        var result = ComparePhysical(left.PhysicalFileKey, right.PhysicalFileKey);
        if (result != 0) return result;
        result = left.ByteLength.CompareTo(right.ByteLength);
        return result != 0 ? result : string.Compare(left.Sha256, right.Sha256, StringComparison.Ordinal);
    }
}

internal sealed record CandidateGroupProjection(
    ImmutableArray<PhysicalFileKey> PhysicalFiles,
    CandidateCategory Category,
    ImmutableArray<ScanFileOccurrence> Members,
    ImmutableArray<EqualityEvidence> Equality,
    bool Uncertain,
    long? KeeperOccurrenceId,
    PotentialRecovery PotentialRecovery);
