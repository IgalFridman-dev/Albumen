using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

internal sealed class ReviewStore
{
    private readonly ScanRunContext context;
    private readonly ImmutableDictionary<Guid, CandidateGroup> originals;
    internal ReviewStore(ReviewSessionPin session, ReviewOpenRequest request)
    {
        context = new(session.Scan, request.Scope.Preflight!.Scope!);
        originals = request.Scan.Groups.ToImmutableDictionary(g => g.GroupId);
        Current = new(session, ReviewStatus.Revalidating, request.Scan.Status, 1,
            request.Scan.Groups.Select(g => new ReviewGroupSnapshot(g,
                g.Members.Select(m => new ReviewMemberSnapshot(m, ReviewEligibilityState.PendingRevalidation,
                    ReviewUnavailableReason.None, false)).ToImmutableArray(), [],
                new(0, SelectionRecoveryUnavailableReason.None), false, null)).ToImmutableArray(),
            new(request.Scan.Enumeration.State == ScanStageState.Succeeded ? 0 : 1,
                request.Scan.Counts.Failed, request.Scan.Counts.Canceled, request.Scan.Counts.Outstanding, 0, 0, 0),
            false, null, ReviewUnavailableReason.None);
        Refresh(Current.Groups);
    }
    internal ReviewSnapshot Current { get; private set; }

    internal bool Accept(ReviewRevalidationEvidence evidence)
    {
        if (!Matches(evidence.Request)) return false;
        var member = evidence.Request.Occurrence;
        var content = member.Content!;
        if (!ValidFacts(member, evidence.Opened) || !ValidFacts(member, evidence.Final)
            || evidence.Digest.ByteLength != content.ByteLength
            || !StringComparer.OrdinalIgnoreCase.Equals(evidence.Digest.Sha256, content.Sha256))
            return Fail(evidence.Request, ReviewUnavailableReason.Changed);
        var allocation = new AllocationEvidence(evidence.Final.Allocation.AllocatedBytes, evidence.Final.Allocation.LinkCount,
            evidence.Opened.Allocation.Stable && evidence.Final.Allocation.Stable
            && evidence.Opened.Allocation.AllocatedBytes == evidence.Final.Allocation.AllocatedBytes
            && evidence.Opened.Allocation.LinkCount == evidence.Final.Allocation.LinkCount);
        var refreshed = new ScanFileOccurrence(member.Token, member.DiscoveredPath, member.ResolvedPath, member.PhysicalFileKey,
            member.Enumerate, member.Exact, member.Content, allocation);
        Refresh(Current.Groups.Select(g => g.Candidate.GroupId != evidence.Request.GroupId ? g : g with
        {
            Members = g.Members.Select(m => m.Occurrence.Token == member.Token
                ? m with { Occurrence = refreshed, Eligibility = ReviewEligibilityState.Eligible, Reason = ReviewUnavailableReason.None } : m).ToImmutableArray(),
        }).ToImmutableArray());
        return true;
    }

    internal bool Fail(ReviewRevalidationRequest request, ReviewUnavailableReason reason)
    {
        if (!Matches(request)) return false;
        if (reason == ReviewUnavailableReason.None || !Enum.IsDefined(reason)) reason = ReviewUnavailableReason.ProgrammingFault;
        var key = request.Occurrence.PhysicalFileKey;
        var changedSelection = Current.Groups.Any(g => g.Members.Any(m => m.Occurrence.PhysicalFileKey == key && m.Selected));
        var groups = Current.Groups.Select(g => g with
        {
            Members = g.Members.Select(m => m.Occurrence.PhysicalFileKey == key
                ? m with { Eligibility = ReviewEligibilityState.Unavailable, Reason = reason, Selected = false } : m).ToImmutableArray(),
            SelectedOccurrenceIds = g.SelectedOccurrenceIds.Except(g.Members.Where(m => m.Occurrence.PhysicalFileKey == key)
                .Select(m => m.Occurrence.Token.OccurrenceId)),
        }).ToImmutableArray();
        if (changedSelection) Current = Current with { SelectionRevision = checked(Current.SelectionRevision + 1) };
        Refresh(groups);
        if (reason == ReviewUnavailableReason.ProgrammingFault) Fault();
        return true;
    }

    private bool Matches(ReviewRevalidationRequest request)
    {
        if (Current.Sealed || Current.Status == ReviewStatus.Faulted || request.Session != Current.Pin
            || request.Context != context || request.Attempt != 1
            || !originals.TryGetValue(request.GroupId, out var group) || request.GroupRevision != group.GroupRevision)
            return false;
        var original = group.Members.FirstOrDefault(m => m.Token.OccurrenceId == request.Occurrence.Token.OccurrenceId);
        return original is not null && original == request.Occurrence && Current.Groups.First(g => g.Candidate.GroupId == group.GroupId).Members
            .Any(m => m.Occurrence.Token == original.Token && m.Eligibility == ReviewEligibilityState.PendingRevalidation);
    }

    private bool ValidFacts(ScanFileOccurrence member, ProtectedFileFacts facts) =>
        facts.PhysicalFileKey == member.PhysicalFileKey && facts.ByteLength == member.Content!.ByteLength
        && ScopePathContainment.Contains(context.Scope.ResolvedPath, facts.ResolvedPath)
        && ScopePathContainment.Contains(member.ResolvedPath, facts.ResolvedPath)
        && ScopePathContainment.Contains(facts.ResolvedPath, member.ResolvedPath);

    internal ReviewCommandResult ChangeSelection(SelectionCommand command)
    {
        if (Current.Status == ReviewStatus.Faulted) return Result(ReviewCommandOutcome.Faulted);
        if (command.Pin.Session != Current.Pin) return Result(ReviewCommandOutcome.Stale);
        if (Current.Sealed) return Result(ReviewCommandOutcome.Sealed);
        var group = Current.Groups.FirstOrDefault(g => g.Candidate.GroupId == command.Pin.GroupId);
        if (group is null || group.Candidate.GroupRevision != command.Pin.GroupRevision
            || Current.SelectionRevision != command.Pin.SelectionRevision) return Result(ReviewCommandOutcome.Stale);
        if (!Enum.IsDefined(command.Kind) || command.CommandId == Guid.Empty || command.OccurrenceIds.IsDefault
            || command.OccurrenceIds.Distinct().Count() != command.OccurrenceIds.Length) return Result(ReviewCommandOutcome.Invalid);
        if (command.OccurrenceIds.Any(id => !group.Members.Any(m => m.Occurrence.Token.OccurrenceId == id)))
            return Result(ReviewCommandOutcome.Stale);
        var eligible = Eligible(group);
        if (command.OccurrenceIds.Any(id => !eligible.Contains(id))) return Result(ReviewCommandOutcome.Invalid);
        var proposed = command.Kind == SelectionCommandKind.ReplaceFocusedGroup ? command.OccurrenceIds.ToImmutableHashSet()
            : group.SelectedOccurrenceIds.SymmetricExcept(command.OccurrenceIds);
        if (proposed.SetEquals(group.SelectedOccurrenceIds)) return Result(ReviewCommandOutcome.Neutral);
        var candidate = ForPolicy(group);
        var decision = SurvivorPolicy.Evaluate(candidate, eligible, proposed);
        if (decision.Kind == SurvivorDecisionKind.OverrideRequired)
            return new(ReviewCommandOutcome.OverrideRequired, Current,
                new(Guid.NewGuid(), command.Pin, proposed.Order().ToImmutableArray(), SurvivorPolicy.Version, "survivor-consequence-v1"));
        if (decision.Kind != SurvivorDecisionKind.Allowed) return Result(ReviewCommandOutcome.Invalid);
        var replacement = group with
        {
            SelectedOccurrenceIds = proposed,
            Members = group.Members.Select(m => m with { Selected = proposed.Contains(m.Occurrence.Token.OccurrenceId) }).ToImmutableArray(),
            OverrideEvidenceId = null,
        };
        var revision = checked(Current.SelectionRevision + 1);
        var groups = Current.Groups.Replace(group, replacement);
        Current = Current with { SelectionRevision = revision };
        Refresh(groups);
        return Result(ReviewCommandOutcome.Committed);
    }

    internal void Finish(ReviewUnavailableReason pendingReason)
    {
        Refresh(Current.Groups.Select(g => g with
        {
            Members = g.Members.Select(m => m.Eligibility == ReviewEligibilityState.PendingRevalidation
                ? m with { Eligibility = ReviewEligibilityState.Unavailable, Reason = pendingReason } : m).ToImmutableArray(),
        }).ToImmutableArray());
    }

    internal void Fault()
    {
        Finish(ReviewUnavailableReason.ProgrammingFault);
        Current = Current with { Status = ReviewStatus.Faulted, FaultReason = ReviewUnavailableReason.ProgrammingFault };
    }

    // Data transition only. Task 6 supplies authorization and the sole public
    // command path; accepting this inert value does not execute an Action.
    internal void Seal(ApprovedActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Current.Sealed || Current.Pin is not { } pin || request.ReviewSessionId != pin.ReviewSessionId
            || request.Scan != pin.Scan || request.SelectionRevision != checked(Current.SelectionRevision + 1))
            throw new InvalidOperationException("The seal must match the current session and next revision.");
        Current = Current with { Sealed = true, ApprovedRequest = request, SelectionRevision = request.SelectionRevision,
            Groups = Current.Groups.Select(g => g with { Sealed = true }).ToImmutableArray() };
    }

    private void Refresh(ImmutableArray<ReviewGroupSnapshot> groups)
    {
        groups = groups.Select(g => g with { Recovery = SelectionAccounting.Calculate(ForPolicy(g), Eligible(g), g.SelectedOccurrenceIds) }).ToImmutableArray();
        var members = groups.SelectMany(g => g.Members).ToArray();
        var pending = members.LongCount(m => m.Eligibility == ReviewEligibilityState.PendingRevalidation);
        Current = Current with
        {
            Groups = groups,
            Status = Current.Status == ReviewStatus.Faulted ? ReviewStatus.Faulted : pending == 0 ? ReviewStatus.Ready : ReviewStatus.Revalidating,
            Unresolved = Current.Unresolved with
            {
                PendingReviewMembers = pending,
                UnavailableReviewMembers = members.LongCount(m => m.Eligibility == ReviewEligibilityState.Unavailable),
                UnselectedSurvivorMembers = members.LongCount(m => m.Eligibility == ReviewEligibilityState.Eligible && !m.Selected),
            },
        };
    }

    private ReviewCommandResult Result(ReviewCommandOutcome outcome) => new(outcome, Current);
    private static ImmutableHashSet<long> Eligible(ReviewGroupSnapshot group) => group.Members
        .Where(m => m.Eligibility == ReviewEligibilityState.Eligible).Select(m => m.Occurrence.Token.OccurrenceId).ToImmutableHashSet();
    private static CandidateGroup ForPolicy(ReviewGroupSnapshot group) => group.Candidate with
    { Members = group.Members.Select(m => m.Occurrence).ToImmutableArray() };
}
