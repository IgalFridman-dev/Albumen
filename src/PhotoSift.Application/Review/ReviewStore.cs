using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

internal sealed class ReviewStore
{
    private const string ConsequenceTextVersion = "survivor-consequence-v1";
    private const string FutureDestination = "Windows Recycle Bin";
    private const string RevalidationNotice =
        "Only the listed, revalidated selected occurrences are included; E1-S5 will revalidate them again before any file operation.";
    private readonly ScanRunContext context;
    private readonly ReviewOpenRequest openRequest;
    private readonly ImmutableDictionary<Guid, CandidateGroup> originals;
    private readonly HashSet<Guid> affirmationOperations = [];
    private readonly Dictionary<Guid, DeliberateOverrideEvidence> attachedOverrideEvidence = [];
    private OverrideProposalToken? issuedProposal;
    private ApproveSelectionCommand? acceptedApproval;
    internal ReviewStore(ReviewSessionPin session, ReviewOpenRequest request)
    {
        openRequest = request;
        context = new(session.Scan, request.Scope.Preflight!.Scope!);
        originals = request.Scan.Groups.ToImmutableDictionary(g => g.GroupId);
        var groups = request.Scan.Groups.Select(g => OpenGroup(g, request)).ToImmutableArray();
        RunGroups = groups.Where(g => !g.Closed).Select(ForPolicy).ToImmutableArray();
        Current = new(session, ReviewStatus.Revalidating, request.Scan.Status, 1, groups,
            new(request.Scan.Enumeration.State == ScanStageState.Succeeded ? 0 : 1,
                request.Scan.Counts.Failed, request.Scan.Counts.Canceled, request.Scan.Counts.Outstanding, 0, 0, 0),
            false, null, ReviewUnavailableReason.None);
        Refresh(Current.Groups);
    }
    internal ReviewSnapshot Current { get; private set; }
    // What the run revalidates: the open sets, each without the photos that left it.
    internal ImmutableArray<CandidateGroup> RunGroups { get; }

    // PM-013: a continuation leaves out the photos the cleanup moved to the Recycle
    // Bin. A set closes when the request names it, when fewer than two photos are
    // left, or when its identical copies no longer span two physical files. Closed
    // members are not under review: nothing revalidates, marks or counts them.
    private static ReviewGroupSnapshot OpenGroup(CandidateGroup group, ReviewOpenRequest request)
    {
        var members = group.Members
            .Where(m => !request.ExcludedOccurrenceIds.Contains(m.Token.OccurrenceId)).ToImmutableArray();
        var closed = request.ClosedGroupIds.Contains(group.GroupId) || members.Length < 2
            || (group.Category == CandidateCategory.Exact
                && members.Select(m => m.PhysicalFileKey).Distinct().Count() < 2);
        return new ReviewGroupSnapshot(group, members.Select(m => closed
                ? new ReviewMemberSnapshot(m, ReviewEligibilityState.Unavailable, ReviewUnavailableReason.Stale, false)
                : new ReviewMemberSnapshot(m, ReviewEligibilityState.PendingRevalidation, ReviewUnavailableReason.None, false))
            .ToImmutableArray(), [], new(0, SelectionRecoveryUnavailableReason.None), false, null) { Closed = closed };
    }

    internal bool MatchesApprovedAction(ApprovedActionRequest request) =>
        Current.Sealed && Current.Status != ReviewStatus.Faulted
        && Current.ApprovedRequest is { } approved && Current.Pin is { } pin
        && approved.ReviewSessionId == pin.ReviewSessionId && approved.Scan == pin.Scan
        && approved.SelectionRevision == Current.SelectionRevision
        && SameActionRequest(approved, request);

    internal static bool SameActionRequest(ApprovedActionRequest left, ApprovedActionRequest right) =>
        left.RequestId == right.RequestId && left.ApprovedUtc.EqualsExact(right.ApprovedUtc)
        && left.ReviewSessionId == right.ReviewSessionId && left.SelectionRevision == right.SelectionRevision
        && left.Scan == right.Scan && left.Scope == right.Scope && left.Disclosure == right.Disclosure
        && !right.Items.IsDefault && !right.SelectedOccurrenceIds.IsDefault
        && left.Items.SequenceEqual(right.Items) && left.SelectedOccurrenceIds.SequenceEqual(right.SelectedOccurrenceIds);

    internal ImmutableArray<CandidateGroup> CaptureActionGroups()
    {
        var selected = Current.ApprovedRequest?.SelectedOccurrenceIds.ToImmutableHashSet()
            ?? GlobalSelection(Current.Groups);
        return Current.Groups.Where(group => !group.Closed && group.Members.Any(member =>
                selected.Contains(member.Occurrence.Token.OccurrenceId)))
            .Select(ForPolicy).ToImmutableArray();
    }

    internal ImmutableArray<DeliberateOverrideEvidence> ActionEvidence => attachedOverrideEvidence.Values
        .OrderBy(evidence => evidence.EvidenceId.Value).ToImmutableArray();

    internal bool MatchesActionEvidence(ImmutableArray<OverrideReadResult> reads)
    {
        var expected = ActionEvidence;
        if (reads.IsDefault || reads.Length != expected.Length || Current.Pin is not { } session) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            var evidence = expected[index];
            var group = Current.Groups.Single(value => value.Candidate.GroupId == evidence.Pin.GroupId);
            if (group.OverrideEvidenceId != evidence.EvidenceId
                || !EvidenceMatchesSelection(evidence, session, group, group.SelectedOccurrenceIds)
                || reads[index] is not { Outcome: OverrideReadOutcome.Found, Evidence: { } actual }
                || !SameEvidence(evidence, actual)) return false;
        }
        return true;
    }

    internal bool Accept(ReviewRevalidationEvidence evidence)
    {
        if (!Matches(evidence.Request)) return false;
        InvalidateProposal();
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
        InvalidateProposal();
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
        if (group.Closed) return Result(ReviewCommandOutcome.Invalid);
        if (!Enum.IsDefined(command.Kind) || command.CommandId == Guid.Empty || command.OccurrenceIds.IsDefault
            || command.OccurrenceIds.Distinct().Count() != command.OccurrenceIds.Length) return Result(ReviewCommandOutcome.Invalid);
        if (command.OccurrenceIds.Any(id => !group.Members.Any(m => m.Occurrence.Token.OccurrenceId == id)))
            return Result(ReviewCommandOutcome.Stale);
        var eligible = Eligible(group);
        if (command.OccurrenceIds.Any(id => !eligible.Contains(id))) return Result(ReviewCommandOutcome.Invalid);
        var proposed = command.Kind == SelectionCommandKind.ReplaceFocusedGroup ? command.OccurrenceIds.ToImmutableHashSet()
            : group.SelectedOccurrenceIds.SymmetricExcept(command.OccurrenceIds);
        if (proposed.SetEquals(group.SelectedOccurrenceIds)) return Result(ReviewCommandOutcome.Neutral);
        var selectedElsewhere = Current.Groups.Where(candidate => candidate != group)
            .SelectMany(candidate => candidate.SelectedOccurrenceIds).ToImmutableHashSet();
        if (proposed.Overlaps(selectedElsewhere)) return Result(ReviewCommandOutcome.Invalid);
        var projectedGroups = Current.Groups.Replace(group, group with
        {
            SelectedOccurrenceIds = proposed,
        });
        var globalSelection = GlobalSelection(projectedGroups);
        var candidate = ForPolicy(group);
        var combined = SelectionFor(group, globalSelection);
        var decision = SurvivorPolicy.Evaluate(candidate, eligible, combined);
        if (!combined.SetEquals(proposed) && decision.Kind != SurvivorDecisionKind.Allowed)
            return Result(ReviewCommandOutcome.Invalid);
        // A closed set holds no marks and takes no projection of the global selection.
        foreach (var other in Current.Groups.Where(candidateGroup => candidateGroup != group && !candidateGroup.Closed))
        {
            var otherCombined = SelectionFor(other, globalSelection);
            if (otherCombined.SetEquals(other.SelectedOccurrenceIds)) continue;
            var otherDecision = SurvivorPolicy.Evaluate(ForPolicy(other), Eligible(other),
                otherCombined);
            if (otherDecision.Kind == SurvivorDecisionKind.Allowed) continue;
            if (otherDecision.Kind != SurvivorDecisionKind.OverrideRequired
                || !otherCombined.SetEquals(other.SelectedOccurrenceIds)
                || other.OverrideEvidenceId is not { } evidenceId
                || !attachedOverrideEvidence.TryGetValue(other.Candidate.GroupId, out var evidence)
                || evidence.EvidenceId != evidenceId
                || Current.Pin is not { } currentPin
                || !EvidenceMatchesSelection(evidence, currentPin, other, otherCombined))
                return Result(ReviewCommandOutcome.Invalid);
        }
        if (decision.Kind == SurvivorDecisionKind.OverrideRequired)
        {
            InvalidateProposal();
            issuedProposal = new(Guid.NewGuid(), command.Pin, proposed.Order().ToImmutableArray(),
                SurvivorPolicy.Version, ConsequenceTextVersion);
            return new(ReviewCommandOutcome.OverrideRequired, Current, issuedProposal);
        }
        if (decision.Kind != SurvivorDecisionKind.Allowed) return Result(ReviewCommandOutcome.Invalid);
        InvalidateProposal();
        attachedOverrideEvidence.Remove(group.Candidate.GroupId);
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

    internal AffirmationStart BeginAffirmation(OverrideAffirmationCommand command)
    {
        if (Current.Status == ReviewStatus.Faulted)
        {
            return new(Result(ReviewCommandOutcome.Faulted), null);
        }

        if (Current.Sealed)
        {
            return new(Result(ReviewCommandOutcome.Sealed), null);
        }

        if (command.EvidenceId.Value == Guid.Empty)
        {
            return new(Result(ReviewCommandOutcome.Invalid), null);
        }

        if (!IsCurrentProposal(command.Proposal, out var group, out var selected))
        {
            return new(Result(ReviewCommandOutcome.Stale), null);
        }

        var affected = ImmutableArray.CreateBuilder<AffectedPhysicalRevision>();
        foreach (var physicalGroup in group.Members
                     .Where(member => selected.Contains(member.Occurrence.Token.OccurrenceId))
                     .GroupBy(member => member.Occurrence.PhysicalFileKey!.Value)
                     .OrderBy(values => values.Key.VolumeSerial)
                     .ThenBy(values => values.Key.FileId.High)
                     .ThenBy(values => values.Key.FileId.Low))
        {
            var observations = physicalGroup.Select(member => member.Occurrence).ToArray();
            var first = observations[0];
            if (first.Content is null || observations.Any(member => member.Content != first.Content
                    || member.Allocation != first.Allocation))
            {
                return new(Result(ReviewCommandOutcome.Invalid), null);
            }

            affected.Add(new(physicalGroup.Key, first.Content, first.Allocation));
        }

        var attempt = new OverridePersistenceAttempt(Guid.NewGuid(), command.Proposal,
            new(command.EvidenceId, command.AffirmedUtc.ToUniversalTime(), command.Proposal.Pin,
                selected.Order().ToImmutableArray(), affected.ToImmutable(),
                command.Proposal.SurvivorPolicyVersion, command.Proposal.ConsequenceTextVersion, true));
        affirmationOperations.Add(attempt.OperationId);
        return new(Result(ReviewCommandOutcome.Neutral), attempt);
    }

    internal ReviewCommandResult CompleteAffirmation(
        OverridePersistenceAttempt attempt,
        OverridePersistResult persisted)
    {
        if (!affirmationOperations.Remove(attempt.OperationId)
            || !IsCurrentProposal(attempt.Proposal, out var group, out var selected))
        {
            return Result(ReviewCommandOutcome.Stale);
        }

        if (persisted.Outcome is OverridePersistOutcome.Conflict or OverridePersistOutcome.Unavailable)
        {
            return Result(ReviewCommandOutcome.PersistenceFailed);
        }

        if (persisted.Outcome is not (OverridePersistOutcome.Persisted or OverridePersistOutcome.ExactRetry))
        {
            Fault();
            return Result(ReviewCommandOutcome.Faulted);
        }

        var replacement = group with
        {
            SelectedOccurrenceIds = selected,
            Members = group.Members.Select(member => member with
            {
                Selected = selected.Contains(member.Occurrence.Token.OccurrenceId),
            }).ToImmutableArray(),
            OverrideEvidenceId = attempt.Evidence.EvidenceId,
        };
        Current = Current with
        {
            SelectionRevision = checked(Current.SelectionRevision + 1),
            Groups = Current.Groups.Replace(group, replacement),
        };
        attachedOverrideEvidence[group.Candidate.GroupId] = attempt.Evidence;
        InvalidateProposal();
        Refresh(Current.Groups);
        return Result(ReviewCommandOutcome.Committed);
    }

    internal void AbandonAffirmation(OverridePersistenceAttempt attempt) =>
        affirmationOperations.Remove(attempt.OperationId);

    internal void Finish(ReviewUnavailableReason pendingReason)
    {
        if (Current.Groups.Any(group => group.Members.Any(member =>
                member.Eligibility == ReviewEligibilityState.PendingRevalidation)))
        {
            InvalidateProposal();
        }

        Refresh(Current.Groups.Select(g => g with
        {
            Members = g.Members.Select(m => m.Eligibility == ReviewEligibilityState.PendingRevalidation
                ? m with { Eligibility = ReviewEligibilityState.Unavailable, Reason = pendingReason } : m).ToImmutableArray(),
        }).ToImmutableArray());
    }

    internal void Fault()
    {
        InvalidateProposal();
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
        InvalidateProposal();
        Current = Current with { Sealed = true, ApprovedRequest = request, SelectionRevision = request.SelectionRevision,
            Groups = Current.Groups.Select(g => g with { Sealed = true }).ToImmutableArray() };
    }

    internal ApprovalStart BeginApproval(ApproveSelectionCommand command)
    {
        var rejection = ValidateApproval(command, out var expectedEvidence);
        if (rejection is not null)
        {
            return new(rejection, null);
        }

        return new(Result(ReviewCommandOutcome.Neutral),
            new ApprovalAttempt(command, expectedEvidence));
    }

    internal ReviewCommandResult CompleteApproval(
        ApprovalAttempt attempt,
        ImmutableArray<OverrideReadResult> reads)
    {
        var rejection = ValidateApproval(attempt.Command, out var expectedEvidence);
        if (rejection is not null)
        {
            return rejection;
        }

        if (expectedEvidence.Length != attempt.ExpectedEvidence.Length
            || reads.Length != expectedEvidence.Length
            || !expectedEvidence.Select(value => value.EvidenceId)
                .SequenceEqual(attempt.ExpectedEvidence.Select(value => value.EvidenceId)))
        {
            return Result(ReviewCommandOutcome.Stale);
        }

        for (var index = 0; index < reads.Length; index++)
        {
            var read = reads[index];
            if (read.Outcome != OverrideReadOutcome.Found || read.Evidence is null
                || !SameEvidence(expectedEvidence[index], read.Evidence))
            {
                return Result(ReviewCommandOutcome.EvidenceUnavailable);
            }
        }

        var selected = Current.Groups
            .SelectMany(group => group.Members
                .Where(member => group.SelectedOccurrenceIds.Contains(
                    member.Occurrence.Token.OccurrenceId))
                .Select(member => (Group: group, Member: member)))
            .OrderBy(value => value.Member.Occurrence.Token.OccurrenceId)
            .ToArray();
        var canonical = new Dictionary<long, (ReviewGroupSnapshot Group, ReviewMemberSnapshot Member)>();
        foreach (var value in selected)
        {
            var id = value.Member.Occurrence.Token.OccurrenceId;
            if (canonical.TryGetValue(id, out var existing))
            {
                if (existing.Member.Occurrence != value.Member.Occurrence)
                    return Result(ReviewCommandOutcome.Invalid);
                continue;
            }
            canonical.Add(id, value);
        }
        var items = canonical.Values.OrderBy(value => value.Member.Occurrence.Token.OccurrenceId)
            .Select(value => new ApprovedActionItem(
                value.Group.Candidate.GroupId,
                value.Group.Candidate.GroupRevision,
                value.Member.Occurrence.Token,
                value.Member.Occurrence.DiscoveredPath,
                value.Member.Occurrence.ResolvedPath,
                value.Member.Occurrence.PhysicalFileKey!.Value,
                value.Member.Occurrence.Content!,
                value.Member.Occurrence.Allocation,
                value.Group.OverrideEvidenceId))
            .ToImmutableArray();
        var selectedIds = items.Select(item => item.Occurrence.OccurrenceId).ToImmutableArray();
        var recovery = ApprovalRecovery(Current.Groups, Current.Pin!.Scan,
            selectedIds.ToImmutableHashSet());
        var disclosure = new ApprovalDisclosure(
            selectedIds.Length,
            items.Select(item => item.PhysicalFileKey).Distinct().LongCount(),
            Current.Coverage,
            Current.Unresolved,
            recovery,
            FutureDestination,
            RevalidationNotice);
        var approved = new ApprovedActionRequest(
            attempt.Command.IdempotencyKey,
            attempt.Command.ApprovedUtc.ToUniversalTime(),
            Current.Pin.ReviewSessionId,
            checked(Current.SelectionRevision + 1),
            Current.Pin.Scan,
            context.Scope,
            items,
            selectedIds,
            disclosure);
        acceptedApproval = attempt.Command;
        Seal(approved);
        return new(ReviewCommandOutcome.Approved, Current, ApprovedRequest: approved);
    }

    private void Refresh(ImmutableArray<ReviewGroupSnapshot> groups)
    {
        var selected = GlobalSelection(groups);
        var holders = SelectionHolders(groups);
        groups = groups.Select(g => g with
        {
            Members = g.Members.Select(m => m with
            {
                SelectedInGroup = !m.Selected
                    && holders.TryGetValue(m.Occurrence.Token.OccurrenceId, out var holder)
                    && holder != g.Candidate.GroupId ? holder : null,
            }).ToImmutableArray(),
            Recovery = SelectionAccounting.Calculate(ForPolicy(g), Eligible(g),
                g.Closed ? ImmutableHashSet<long>.Empty : SelectionFor(g, selected)),
        }).ToImmutableArray();
        var counts = ReviewMemberCounts(groups, selected);
        Current = Current with
        {
            Groups = groups,
            Status = Current.Status == ReviewStatus.Faulted ? ReviewStatus.Faulted
                : counts.Pending == 0 ? ReviewStatus.Ready : ReviewStatus.Revalidating,
            Unresolved = Current.Unresolved with
            {
                PendingReviewMembers = counts.Pending,
                UnavailableReviewMembers = counts.Unavailable,
                UnselectedSurvivorMembers = counts.Unselected,
            },
        };
    }

    private ReviewCommandResult Result(ReviewCommandOutcome outcome) => new(outcome, Current);

    private ReviewCommandResult? ValidateApproval(
        ApproveSelectionCommand command,
        out ImmutableArray<DeliberateOverrideEvidence> expectedEvidence)
    {
        expectedEvidence = [];
        if (Current.Status == ReviewStatus.Faulted)
        {
            return Result(ReviewCommandOutcome.Faulted);
        }

        if (Current.Pin is not { } pin || command.Session != pin)
        {
            return Result(ReviewCommandOutcome.Stale);
        }

        if (Current.Sealed)
        {
            return acceptedApproval == command && Current.ApprovedRequest is { } approved
                ? new(ReviewCommandOutcome.Approved, Current, ApprovedRequest: approved)
                : Result(ReviewCommandOutcome.Sealed);
        }

        if (command.SelectionRevision != Current.SelectionRevision)
        {
            return Result(ReviewCommandOutcome.Stale);
        }

        if (command.IdempotencyKey == Guid.Empty
            || Current.Coverage is not (ScanStatus.Complete or ScanStatus.Incomplete)
            || Current.Status is not (ReviewStatus.Ready or ReviewStatus.Revalidating)
            || !ValidUnresolvedSummary())
        {
            return Result(ReviewCommandOutcome.Invalid);
        }

        var expected = ImmutableArray.CreateBuilder<DeliberateOverrideEvidence>();
        var selectedCount = 0;
        var selectedIds = new HashSet<long>();
        var globalSelection = GlobalSelection(Current.Groups);
        foreach (var selectedId in globalSelection)
        {
            var copies = Current.Groups.Where(group => !group.Closed).SelectMany(group => group.Members)
                .Where(member => member.Occurrence.Token.OccurrenceId == selectedId)
                .ToArray();
            if (copies.Length == 0 || copies.Any(member =>
                    member.Eligibility != ReviewEligibilityState.Eligible
                    || member.Reason != ReviewUnavailableReason.None
                    || member.Occurrence != copies[0].Occurrence))
                return Result(ReviewCommandOutcome.Invalid);
        }
        foreach (var group in Current.Groups)
        {
            if (!originals.TryGetValue(group.Candidate.GroupId, out var original)
                || group.Candidate != original
                || group.Candidate.Pin != pin.Scan
                || group.Candidate.GroupRevision <= 0
                || group.Sealed
                || group.SelectedOccurrenceIds.Any(id => !selectedIds.Add(id))
                || group.Members.Any(member => member.Selected
                    != group.SelectedOccurrenceIds.Contains(member.Occurrence.Token.OccurrenceId)))
            {
                return Result(ReviewCommandOutcome.Stale);
            }

            if (group.Closed)
            {
                if (!group.SelectedOccurrenceIds.IsEmpty || group.OverrideEvidenceId is not null
                    || attachedOverrideEvidence.ContainsKey(group.Candidate.GroupId))
                {
                    return Result(ReviewCommandOutcome.Invalid);
                }

                continue;
            }

            var eligible = Eligible(group);
            var selected = group.SelectedOccurrenceIds;
            var combined = SelectionFor(group, globalSelection);
            if (!selected.IsSubsetOf(eligible)
                || group.Recovery != SelectionAccounting.Calculate(ForPolicy(group), eligible, combined))
            {
                return Result(ReviewCommandOutcome.Invalid);
            }

            foreach (var selectedId in selected)
            {
                var member = group.Members.FirstOrDefault(value =>
                    value.Occurrence.Token.OccurrenceId == selectedId);
                var originalMember = original.Members.FirstOrDefault(value =>
                    value.Token.OccurrenceId == selectedId);
                if (member is null || originalMember is null
                    || member.Eligibility != ReviewEligibilityState.Eligible
                    || member.Reason != ReviewUnavailableReason.None
                    || member.Occurrence.Token != originalMember.Token
                    || member.Occurrence.Token.Pin != pin.Scan
                    || member.Occurrence.Token.OccurrenceRevision <= 0
                    || member.Occurrence.DiscoveredPath != originalMember.DiscoveredPath
                    || member.Occurrence.ResolvedPath != originalMember.ResolvedPath
                    || member.Occurrence.PhysicalFileKey is null
                    || member.Occurrence.PhysicalFileKey != originalMember.PhysicalFileKey
                    || member.Occurrence.Content is null
                    || member.Occurrence.Content != originalMember.Content)
                {
                    return Result(ReviewCommandOutcome.Stale);
                }
            }

            selectedCount = checked(selectedCount + selected.Count);
            if (selected.Count == 0)
            {
                if (group.OverrideEvidenceId is not null
                    || attachedOverrideEvidence.ContainsKey(group.Candidate.GroupId)
                    || !combined.IsEmpty
                    && SurvivorPolicy.Evaluate(ForPolicy(group), eligible, combined).Kind
                        != SurvivorDecisionKind.Allowed)
                {
                    return Result(ReviewCommandOutcome.Invalid);
                }

                continue;
            }

            var survivor = SurvivorPolicy.Evaluate(ForPolicy(group), eligible, combined);
            var hasEvidence = attachedOverrideEvidence.TryGetValue(
                group.Candidate.GroupId, out var evidence);
            if (group.OverrideEvidenceId is not null || hasEvidence)
            {
                if (group.OverrideEvidenceId is not { } attachedEvidenceId
                    || !hasEvidence
                    || evidence!.EvidenceId != attachedEvidenceId
                    || !combined.SetEquals(selected)
                    || !EvidenceMatchesSelection(evidence, pin, group, selected)
                    || survivor.Kind is not (SurvivorDecisionKind.Allowed
                        or SurvivorDecisionKind.OverrideRequired))
                {
                    return Result(ReviewCommandOutcome.Invalid);
                }

                expected.Add(evidence);
            }
            else if (survivor.Kind != SurvivorDecisionKind.Allowed)
            {
                return Result(ReviewCommandOutcome.Invalid);
            }
        }

        if (selectedCount == 0)
        {
            return Result(ReviewCommandOutcome.Invalid);
        }

        expectedEvidence = expected
            .OrderBy(value => value.EvidenceId.Value)
            .ToImmutableArray();
        return null;
    }

    private bool ValidUnresolvedSummary()
    {
        var values = new[]
        {
            Current.Unresolved.IncompleteEnumeration,
            Current.Unresolved.FailedScanRows,
            Current.Unresolved.CanceledScanRows,
            Current.Unresolved.UnprocessedScanRows,
            Current.Unresolved.PendingReviewMembers,
            Current.Unresolved.UnavailableReviewMembers,
            Current.Unresolved.UnselectedSurvivorMembers,
        };
        if (values.Any(value => value < 0))
        {
            return false;
        }

        try
        {
            _ = Current.Unresolved.Total;
        }
        catch (OverflowException)
        {
            return false;
        }

        var memberCounts = ReviewMemberCounts(Current.Groups,
            GlobalSelection(Current.Groups));
        var expected = new ReviewUnresolvedSummary(
            openRequest.Scan.Enumeration.State == ScanStageState.Succeeded ? 0 : 1,
            openRequest.Scan.Counts.Failed,
            openRequest.Scan.Counts.Canceled,
            openRequest.Scan.Counts.Outstanding,
            memberCounts.Pending,
            memberCounts.Unavailable,
            memberCounts.Unselected);
        if (Current.Unresolved != expected)
        {
            return false;
        }

        return Current.Coverage == ScanStatus.Complete
            ? openRequest.Scan.Enumeration.State == ScanStageState.Succeeded
              && openRequest.Scan.Counts.Canceled == 0
              && openRequest.Scan.Counts.Outstanding == 0
            : expected.IncompleteEnumeration > 0
              || expected.FailedScanRows > 0
              || expected.CanceledScanRows > 0
              || expected.UnprocessedScanRows > 0
              || expected.PendingReviewMembers > 0
              || expected.UnavailableReviewMembers > 0;
    }

    private static SelectionRecovery ApprovalRecovery(
        ImmutableArray<ReviewGroupSnapshot> groups,
        ScanPin scan,
        ImmutableHashSet<long> selected)
    {
        var membersById = new Dictionary<long, ScanFileOccurrence>();
        foreach (var member in groups.SelectMany(group => group.Members)
                     .Select(member => member.Occurrence))
        {
            var id = member.Token.OccurrenceId;
            if (membersById.TryGetValue(id, out var existing) && existing != member)
                return new(null, SelectionRecoveryUnavailableReason.InconsistentFacts);
            membersById[id] = member;
        }
        var members = membersById.Values.OrderBy(member => member.Token.OccurrenceId)
            .ToImmutableArray();
        var eligible = groups.SelectMany(group => group.Members)
            .Where(member => member.Eligibility == ReviewEligibilityState.Eligible)
            .Select(member => member.Occurrence.Token.OccurrenceId)
            .ToImmutableHashSet();
        var combined = new CandidateGroup(Guid.Empty, 1, scan,
            CandidateCategory.Exact, members, [], false, null,
            new(null, RecoveryUnavailableReason.InconsistentFacts));
        return SelectionAccounting.Calculate(combined, eligible, selected);
    }

    private static bool SameEvidence(
        DeliberateOverrideEvidence expected,
        DeliberateOverrideEvidence actual) =>
        expected.EvidenceId == actual.EvidenceId
        && expected.AffirmedUtc.EqualsExact(actual.AffirmedUtc)
        && expected.Pin == actual.Pin
        && expected.SelectedOccurrenceIds.SequenceEqual(actual.SelectedOccurrenceIds)
        && expected.AffectedPhysicalRevisions.SequenceEqual(actual.AffectedPhysicalRevisions)
        && StringComparer.Ordinal.Equals(expected.SurvivorPolicyVersion,
            actual.SurvivorPolicyVersion)
        && StringComparer.Ordinal.Equals(expected.ConsequenceTextVersion,
            actual.ConsequenceTextVersion)
        && expected.DeliberatelyAffirmed == actual.DeliberatelyAffirmed;

    private static bool EvidenceMatchesSelection(
        DeliberateOverrideEvidence evidence,
        ReviewSessionPin session,
        ReviewGroupSnapshot group,
        ImmutableHashSet<long> selected)
    {
        if (evidence.Pin.Session != session
            || evidence.Pin.GroupId != group.Candidate.GroupId
            || evidence.Pin.GroupRevision != group.Candidate.GroupRevision
            || evidence.Pin.SelectionRevision <= 0
            || evidence.SelectedOccurrenceIds.IsDefault
            || !evidence.SelectedOccurrenceIds.SequenceEqual(selected.Order())
            || !StringComparer.Ordinal.Equals(evidence.SurvivorPolicyVersion,
                SurvivorPolicy.Version)
            || !StringComparer.Ordinal.Equals(evidence.ConsequenceTextVersion,
                ConsequenceTextVersion)
            || !evidence.DeliberatelyAffirmed)
        {
            return false;
        }

        var affected = ImmutableArray.CreateBuilder<AffectedPhysicalRevision>();
        foreach (var physicalGroup in group.Members
                     .Where(member => selected.Contains(
                         member.Occurrence.Token.OccurrenceId))
                     .GroupBy(member => member.Occurrence.PhysicalFileKey!.Value)
                     .OrderBy(values => values.Key.VolumeSerial)
                     .ThenBy(values => values.Key.FileId.High)
                     .ThenBy(values => values.Key.FileId.Low))
        {
            var observations = physicalGroup.Select(member => member.Occurrence).ToArray();
            var first = observations[0];
            if (first.Content is null || observations.Any(member =>
                    member.Content != first.Content || member.Allocation != first.Allocation))
            {
                return false;
            }

            affected.Add(new(physicalGroup.Key, first.Content, first.Allocation));
        }

        return evidence.AffectedPhysicalRevisions.SequenceEqual(affected);
    }

    private bool IsCurrentProposal(
        OverrideProposalToken proposal,
        out ReviewGroupSnapshot group,
        out ImmutableHashSet<long> selected)
    {
        group = null!;
        selected = [];
        if (issuedProposal is null || !SameProposal(issuedProposal, proposal)
            || Current.Pin != proposal.Pin.Session
            || Current.SelectionRevision != proposal.Pin.SelectionRevision)
        {
            return false;
        }

        group = Current.Groups.FirstOrDefault(value => value.Candidate.GroupId == proposal.Pin.GroupId)!;
        if (group is null || group.Candidate.GroupRevision != proposal.Pin.GroupRevision)
        {
            return false;
        }

        selected = proposal.ProposedSelectedOccurrenceIds.ToImmutableHashSet();
        if (selected.Count != proposal.ProposedSelectedOccurrenceIds.Length)
        {
            return false;
        }

        var eligible = Eligible(group);
        return selected.IsSubsetOf(eligible)
            && SurvivorPolicy.Evaluate(ForPolicy(group), eligible, selected).Kind == SurvivorDecisionKind.OverrideRequired;
    }

    private void InvalidateProposal()
    {
        issuedProposal = null;
        affirmationOperations.Clear();
    }

    private static bool SameProposal(OverrideProposalToken left, OverrideProposalToken right) =>
        left.ProposalId == right.ProposalId
        && left.Pin == right.Pin
        && StringComparer.Ordinal.Equals(left.SurvivorPolicyVersion, right.SurvivorPolicyVersion)
        && StringComparer.Ordinal.Equals(left.ConsequenceTextVersion, right.ConsequenceTextVersion)
        && left.ProposedSelectedOccurrenceIds.SequenceEqual(right.ProposedSelectedOccurrenceIds);
    private static ImmutableHashSet<long> Eligible(ReviewGroupSnapshot group) => group.Members
        .Where(m => m.Eligibility == ReviewEligibilityState.Eligible).Select(m => m.Occurrence.Token.OccurrenceId).ToImmutableHashSet();
    private static ImmutableHashSet<long> GlobalSelection(
        ImmutableArray<ReviewGroupSnapshot> groups) => groups
        .SelectMany(group => group.SelectedOccurrenceIds).ToImmutableHashSet();
    private static ImmutableDictionary<long, Guid> SelectionHolders(
        ImmutableArray<ReviewGroupSnapshot> groups)
    {
        var holders = ImmutableDictionary.CreateBuilder<long, Guid>();
        foreach (var group in groups)
        foreach (var id in group.SelectedOccurrenceIds)
            holders.TryAdd(id, group.Candidate.GroupId);
        return holders.ToImmutable();
    }
    private static ImmutableHashSet<long> SelectionFor(
        ReviewGroupSnapshot group,
        ImmutableHashSet<long> selected) => group.Members
        .Where(member => selected.Contains(member.Occurrence.Token.OccurrenceId))
        .Select(member => member.Occurrence.Token.OccurrenceId).ToImmutableHashSet();
    private static (long Pending, long Unavailable, long Unselected) ReviewMemberCounts(
        ImmutableArray<ReviewGroupSnapshot> groups,
        ImmutableHashSet<long> selected)
    {
        long pending = 0;
        long unavailable = 0;
        long unselected = 0;
        foreach (var occurrence in groups.Where(group => !group.Closed).SelectMany(group => group.Members)
                     .GroupBy(member => member.Occurrence.Token.OccurrenceId))
        {
            if (occurrence.Any(member => member.Eligibility == ReviewEligibilityState.PendingRevalidation))
                pending++;
            else if (occurrence.Any(member => member.Eligibility == ReviewEligibilityState.Unavailable))
                unavailable++;
            else if (!selected.Contains(occurrence.Key)) unselected++;
        }
        return (pending, unavailable, unselected);
    }
    private static CandidateGroup ForPolicy(ReviewGroupSnapshot group) => group.Candidate with
    { Members = group.Members.Select(m => m.Occurrence).ToImmutableArray() };
}

internal sealed record OverridePersistenceAttempt(
    Guid OperationId,
    OverrideProposalToken Proposal,
    DeliberateOverrideEvidence Evidence);

internal sealed record AffirmationStart(
    ReviewCommandResult Result,
    OverridePersistenceAttempt? Attempt);

internal sealed record ApprovalAttempt(
    ApproveSelectionCommand Command,
    ImmutableArray<DeliberateOverrideEvidence> ExpectedEvidence);

internal sealed record ApprovalStart(
    ReviewCommandResult Result,
    ApprovalAttempt? Attempt);
