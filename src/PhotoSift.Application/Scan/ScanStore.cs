using System.Collections.Immutable;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Scan;

internal sealed class ScanStore(ScanRunContext context)
{
    private static readonly StageOutcome Running = new(ScanStageState.Running, ScanReason.None);
    private static readonly StageOutcome Success = new(ScanStageState.Succeeded, ScanReason.None);
    private static readonly StageOutcome Changed = new(ScanStageState.FailedRetryable, ScanReason.Changed);
    private readonly Dictionary<long, ScanFileOccurrence> occurrences = [];
    private readonly Dictionary<PhysicalFileKey, StageOutcome> invalidPhysical = [];
    private readonly ExactGroupIndex groups = new();
    private long nextId;

    public bool Incomplete { get; private set; }
    public bool Faulted { get; private set; }
    public int PendingComparisons => groups.PendingCount;

    public OccurrenceWork? Register(EnumeratedFile file)
    {
        var token = new OccurrenceToken(context.Pin, ++nextId, 1, 1);
        var exact = file.Outcome.State == ScanStageState.Succeeded ? Running : file.Outcome;
        if (file.Outcome.State == ScanStageState.Succeeded && file.PhysicalFileKey is null)
            exact = new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety);
        if (file.PhysicalFileKey is { } key && invalidPhysical.TryGetValue(key, out var invalid)) exact = invalid;
        var row = new ScanFileOccurrence(token, file.DiscoveredPath, file.ResolvedPath, file.PhysicalFileKey,
            file.Outcome, exact, null, new AllocationEvidence(null, null, false));
        occurrences.Add(token.OccurrenceId, row);
        Observe(exact);
        return exact.State == ScanStageState.Running ? new OccurrenceWork(context, token, file) : null;
    }

    public SignatureCommit CommitSignature(SignatureEvidence evidence)
    {
        if (!Matches(evidence.Token, ScanStageState.Running)) return new SignatureCommit(false, []);
        var row = occurrences[evidence.Token.OccurrenceId];
        var work = Work(row);
        if (!ValidFacts(work, evidence.Opened, evidence.Content)
            || !ValidFacts(work, evidence.Final, evidence.Content))
        {
            Invalidate(row.PhysicalFileKey!.Value, Changed);
            return new SignatureCommit(false, []);
        }
        var previous = groups.ContentFor(evidence.Content.PhysicalFileKey);
        if (previous is not null && previous != evidence.Content)
        {
            Invalidate(evidence.Content.PhysicalFileKey, Changed);
            return new SignatureCommit(false, []);
        }
        occurrences[row.Token.OccurrenceId] = Update(row, Success, evidence.Content,
            Allocation(evidence.Opened.Allocation, evidence.Final.Allocation));
        return new SignatureCommit(true, groups.Add(work, evidence.Content));
    }

    public bool CommitComparison(ComparisonEvidence evidence)
    {
        var work = evidence.Work;
        if (!groups.IsPending(work) || !MatchesContent(work.Left.Token, work.LeftContent)
            || !MatchesContent(work.Right.Token, work.RightContent)) return false;
        var leftValid = ValidFacts(work.Left, evidence.LeftFinal, work.LeftContent)
            && ValidDigest(evidence.Content.Left, work.LeftContent);
        var rightValid = ValidFacts(work.Right, evidence.RightFinal, work.RightContent)
            && ValidDigest(evidence.Content.Right, work.RightContent);
        groups.Settle(work);
        if (!leftValid) Invalidate(work.LeftContent.PhysicalFileKey, Changed);
        if (!rightValid) Invalidate(work.RightContent.PhysicalFileKey, Changed);
        if (!leftValid || !rightValid) return false;
        if (evidence.Equal != evidence.Content.Equal)
            throw new InvalidOperationException("Comparison and stream equality disagree.");
        RefreshAllocation(work.Left.Token, evidence.LeftFinal.Allocation);
        RefreshAllocation(work.Right.Token, evidence.RightFinal.Allocation);
        if (evidence.Equal) groups.Accept(work);
        return true;
    }

    public bool Fail(OccurrenceToken token, StageOutcome outcome)
    {
        if (outcome.State is ScanStageState.Pending or ScanStageState.Running or ScanStageState.Succeeded)
            throw new ArgumentException("Failure must have a terminal unsuccessful outcome.", nameof(outcome));
        if (Matches(token, ScanStageState.Running))
        {
            var row = occurrences[token.OccurrenceId];
            if (outcome.Reason == ScanReason.Changed && row.PhysicalFileKey is { } key) Invalidate(key, outcome);
            else { occurrences[token.OccurrenceId] = Update(row, outcome, null, row.Allocation); Observe(outcome); }
            return true;
        }
        if (!Matches(token, ScanStageState.Succeeded) || !groups.HasPending(token)) return false;
        if (outcome.State == ScanStageState.Canceled)
        {
            groups.Settle(token);
            Observe(outcome);
        }
        else Invalidate(occurrences[token.OccurrenceId].PhysicalFileKey!.Value, outcome);
        return true;
    }

    // A worker may stop before opening later returned comparison descriptors.
    // Finalization settles those tickets without erasing already verified bytes.
    public void Reconcile(StageOutcome outcome)
    {
        var unresolved = groups.PendingCount != 0;
        groups.SettleAll();
        foreach (var row in occurrences.Values.ToArray())
        {
            if (row.Exact.State is ScanStageState.Pending or ScanStageState.Running)
            {
                occurrences[row.Token.OccurrenceId] = Update(row, outcome, null, row.Allocation);
                unresolved = true;
            }
        }
        if (unresolved) { Incomplete = true; Observe(outcome); }
    }

    public ScanSnapshot Snapshot(ScanStatus status, StageOutcome enumeration)
    {
        var rows = occurrences.Values.OrderBy(row => row.Token.OccurrenceId).ToImmutableArray();
        return ScanSnapshotProjector.Create(context.Pin, status, enumeration, rows, groups.Groups(context.Pin, rows));
    }

    private bool Matches(OccurrenceToken token, ScanStageState state) => token.Pin == context.Pin
        && occurrences.TryGetValue(token.OccurrenceId, out var row) && row.Token == token && row.Exact.State == state;
    private bool MatchesContent(OccurrenceToken token, ContentRevision content) =>
        Matches(token, ScanStageState.Succeeded) && occurrences[token.OccurrenceId].Content == content;
    private bool ValidFacts(OccurrenceWork work, ProtectedFileFacts facts, ContentRevision content) =>
        facts.PhysicalFileKey == work.File.PhysicalFileKey && facts.PhysicalFileKey == content.PhysicalFileKey
        && facts.ByteLength == content.ByteLength
        && ScopePathContainment.Contains(context.Scope.ResolvedPath, facts.ResolvedPath)
        && ScopePathContainment.Contains(work.File.ResolvedPath, facts.ResolvedPath)
        && ScopePathContainment.Contains(facts.ResolvedPath, work.File.ResolvedPath);
    private static bool ValidDigest(StreamDigest digest, ContentRevision content) =>
        digest.ByteLength == content.ByteLength && string.Equals(digest.Sha256, content.Sha256, StringComparison.OrdinalIgnoreCase);
    private OccurrenceWork Work(ScanFileOccurrence row) => new(context, row.Token,
        new EnumeratedFile(row.DiscoveredPath, row.ResolvedPath, row.PhysicalFileKey, row.Enumerate));
    private static ScanFileOccurrence Update(ScanFileOccurrence row, StageOutcome outcome, ContentRevision? content,
        AllocationEvidence allocation) => new(row.Token, row.DiscoveredPath, row.ResolvedPath,
            row.PhysicalFileKey, row.Enumerate, outcome, content, allocation);

    private void Invalidate(PhysicalFileKey key, StageOutcome outcome)
    {
        invalidPhysical[key] = outcome;
        groups.Remove(key);
        foreach (var row in occurrences.Values.Where(row => row.PhysicalFileKey == key).ToArray())
            occurrences[row.Token.OccurrenceId] = Update(row, outcome, null, row.Allocation);
        Observe(outcome);
    }

    private void RefreshAllocation(OccurrenceToken token, AllocationEvidence allocation)
    {
        var row = occurrences[token.OccurrenceId];
        occurrences[token.OccurrenceId] = Update(row, row.Exact, row.Content, Allocation(row.Allocation, allocation));
    }
    private static AllocationEvidence Allocation(AllocationEvidence before, AllocationEvidence after) =>
        new(after.AllocatedBytes, after.LinkCount, before.Stable && after.Stable
            && before.AllocatedBytes == after.AllocatedBytes && before.LinkCount == after.LinkCount);
    private void Observe(StageOutcome outcome)
    {
        if (outcome.Reason == ScanReason.ProgrammingFault) Faulted = true;
        if (outcome.Reason is ScanReason.Changed or ScanReason.Canceled or ScanReason.UnsafeLocation
            or ScanReason.UnknownSafety or ScanReason.IncompleteEnumeration) Incomplete = true;
    }
}
