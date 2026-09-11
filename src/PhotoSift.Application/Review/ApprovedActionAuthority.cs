using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Review;

// This is part of the Review owner. It reserves Review authority only; Action
// execution, journal transitions, and item state belong to the Action owner.
public sealed partial class ReviewCoordinator
{
    private readonly ProtectedActionSafetyValidator? actionSafety;
    private readonly HashSet<Task> actionEffects = [];
    private readonly HashSet<Guid> safetyRefreshes = [];

    public async Task<ApprovedActionClaimResult> ClaimAsync(ApprovedActionRequest request,
        ImmutableArray<OverrideReadResult> overrideReads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rejection = ActionRejection(active, request);
            if (rejection is not null) return new ClaimStart(Task.FromResult(rejection), null, null);
            var operation = active!;
            if (!operation.Store.MatchesActionEvidence(overrideReads))
                return new ClaimStart(Task.FromResult(ClaimFailure(ApprovedActionClaimOutcome.EvidenceUnavailable,
                    ActionSafetyReason.OverrideMismatch)), null, null);
            // Even after the acceptance turn, duplicate callers wait for the
            // original effect's acknowledgment and lease cleanup to settle.
            if (operation.ClaimAttempt is { } reserved)
                return new ClaimStart(reserved.Completion.Task, null, null);
            if (operation.Claim is { } claim)
                return new ClaimStart(Task.FromResult(new ApprovedActionClaimResult(
                    ApprovedActionClaimOutcome.Existing, claim, ActionSafetyReason.None)), null, null);
            var approved = operation.Store.Current.ApprovedRequest!;
            var attempt = new ActionClaimAttempt(Guid.NewGuid(), approved,
                new(approved.Scan, approved.Scope), operation.Store.CaptureActionGroups(), overrideReads,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            operation.ClaimAttempt = attempt;
            actionEffects.Add(attempt.Completion.Task);
            return new ClaimStart(attempt.Completion.Task, operation, attempt);
        }).ConfigureAwait(false);
        if (start.Operation is { } operation && start.Attempt is { } attempt)
            _ = Task.Run(() => RunClaimAsync(operation, attempt, cancellationToken), CancellationToken.None);
        return await start.Completion.ConfigureAwait(false);
    }

    private async Task RunClaimAsync(Operation operation, ActionClaimAttempt attempt, CancellationToken token)
    {
        ApprovedActionClaimResult result;
        try
        {
            var reads = await ReadActionEvidenceAsync(attempt.Reads.Select(read => read.Evidence!).ToImmutableArray(), token).ConfigureAwait(false);
            if (actionSafety is null)
                result = await inbox.ResultAsync(() => ActionRejection(operation, attempt.Request)
                    ?? ClaimFailure(ApprovedActionClaimOutcome.UnsafeCurrentFacts, ActionSafetyReason.Unavailable)).ConfigureAwait(false);
            else
                result = await actionSafety.ProbeAsync(attempt.Context, attempt.Groups, [],
                    (groups, reason) => inbox.ResultAsync(() => CompleteActionClaim(operation, attempt, reads, groups, reason)), token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = await inbox.CommandAsync(() =>
            {
                var rejection = ActionRejection(operation, attempt.Request);
                if (rejection is not null) return rejection;
                Fault(operation);
                return ClaimFailure(ApprovedActionClaimOutcome.Faulted, ActionSafetyReason.ProgrammingFault);
            }).ConfigureAwait(false);
        }
        await inbox.CommandAsync(() =>
        {
            if (ReferenceEquals(operation.ClaimAttempt, attempt)) operation.ClaimAttempt = null;
            actionEffects.Remove(attempt.Completion.Task);
            attempt.Completion.TrySetResult(result);
            return true;
        }).ConfigureAwait(false);
    }

    private ApprovedActionClaimResult CompleteActionClaim(Operation operation, ActionClaimAttempt attempt,
        ImmutableArray<OverrideReadResult> reads, ImmutableArray<ActionGroupProbeResult> groups, ActionSafetyReason reason)
    {
        var rejection = ActionRejection(operation, attempt.Request);
        if (rejection is not null) return rejection;
        if (!ReferenceEquals(operation.ClaimAttempt, attempt))
            return ClaimFailure(ApprovedActionClaimOutcome.Stale, ActionSafetyReason.StalePin);
        if (!operation.Store.MatchesActionEvidence(attempt.Reads) || !operation.Store.MatchesActionEvidence(reads))
            return ClaimFailure(ApprovedActionClaimOutcome.EvidenceUnavailable, ActionSafetyReason.OverrideMismatch);
        if (reason != ActionSafetyReason.None) return ProbeFailure(operation, reason);
        foreach (var group in attempt.Groups)
        {
            var input = SafetyInput(attempt.Request, group, groups.Single(probe => probe.GroupId == group.GroupId), []);
            var decision = ActionSafetyPolicy.Evaluate(input);
            if (decision.Kind != ActionSafetyDecisionKind.Allowed)
                return ClaimFailure(ApprovedActionClaimOutcome.UnsafeCurrentFacts, decision.Reason);
        }
        var claim = new ApprovedActionClaim(new(Guid.NewGuid()), attempt.Request);
        operation.Claim = claim;
        return new(ApprovedActionClaimOutcome.Claimed, claim, ActionSafetyReason.None);
    }

    public async Task<ActionSafetyRefreshResult> RefreshSafetyAsync(ApprovedActionClaim claim,
        ApprovedActionItem item, ImmutableArray<PriorActionOutcome> precedingOutcomes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(item);
        var start = await inbox.CommandAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rejection = ActionRejection(active, claim.Request);
            if (rejection is not null) return new RefreshStart(new(rejection.Outcome, null, rejection.Reason), null, null);
            var operation = active!;
            if (operation.Claim is not { } accepted || accepted.ClaimId != claim.ClaimId
                || !ReviewStore.SameActionRequest(accepted.Request, claim.Request)
                || !ValidPrecedingOutcomes(accepted.Request, item, precedingOutcomes))
                return new RefreshStart(new(ApprovedActionClaimOutcome.Stale, null, ActionSafetyReason.StalePin), null, null);
            var canonicalItem = accepted.Request.Items.Single(value => value == item);
            var group = operation.Store.CaptureActionGroups().Single(value => value.GroupId == item.GroupId);
            var attempt = new ActionSafetyRefreshAttempt(Guid.NewGuid(), accepted, canonicalItem,
                new(accepted.Request.Scan, accepted.Request.Scope), group, precedingOutcomes,
                operation.Store.ActionEvidence, new(TaskCreationOptions.RunContinuationsAsynchronously));
            safetyRefreshes.Add(attempt.AttemptId);
            actionEffects.Add(attempt.Settled.Task);
            return new RefreshStart(null, operation, attempt);
        }).ConfigureAwait(false);
        if (start.Attempt is not { } attempt || start.Operation is not { } operation) return start.Result!;
        // Synchronous opens/analyzer entry points must never run on an owner or
        // observer callback, including when every await completes immediately.
        return await Task.Run(() => RunSafetyRefreshAsync(operation, attempt, cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ActionSafetyRefreshResult> RunSafetyRefreshAsync(Operation operation,
        ActionSafetyRefreshAttempt attempt, CancellationToken token)
    {
        try
        {
            var reads = await ReadActionEvidenceAsync(attempt.ExpectedEvidence, token).ConfigureAwait(false);
            var recycled = attempt.PrecedingOutcomes.Where(outcome => outcome.GroupId == attempt.Group.GroupId
                && outcome.Disposition == PriorActionDisposition.Recycled).Select(outcome => outcome.OccurrenceId).ToImmutableHashSet();
            if (actionSafety is null)
                return new(ApprovedActionClaimOutcome.UnsafeCurrentFacts, null, ActionSafetyReason.Unavailable);
            return await actionSafety.ProbeAsync(attempt.Context, [attempt.Group], recycled,
                (groups, reason) => inbox.ResultAsync(() =>
                {
                    var rejection = ActionRejection(operation, attempt.Claim.Request);
                    if (rejection is not null) return new ActionSafetyRefreshResult(rejection.Outcome, null, rejection.Reason);
                    if (!safetyRefreshes.Contains(attempt.AttemptId) || !ReferenceEquals(operation.Claim, attempt.Claim))
                        return new(ApprovedActionClaimOutcome.Stale, null, ActionSafetyReason.StalePin);
                    if (!operation.Store.MatchesActionEvidence(reads))
                        return new(ApprovedActionClaimOutcome.EvidenceUnavailable, null, ActionSafetyReason.OverrideMismatch);
                    if (reason != ActionSafetyReason.None)
                    {
                        var failed = ProbeFailure(operation, reason);
                        return new(failed.Outcome, null, failed.Reason);
                    }
                    var probe = groups.Single();
                    var input = SafetyInput(attempt.Claim.Request, attempt.Group, probe, attempt.PrecedingOutcomes);
                    var decision = ActionSafetyPolicy.Evaluate(input);
                    if (decision.Kind != ActionSafetyDecisionKind.Allowed)
                        return new(ApprovedActionClaimOutcome.UnsafeCurrentFacts, null, decision.Reason);
                    return new(ApprovedActionClaimOutcome.Claimed,
                        new(attempt.Claim, attempt.Item, input, decision, probe.Observations, attempt.PrecedingOutcomes), ActionSafetyReason.None);
                }), token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await inbox.CommandAsync(() =>
            {
                var rejection = ActionRejection(operation, attempt.Claim.Request);
                if (rejection is not null) return new ActionSafetyRefreshResult(rejection.Outcome, null, rejection.Reason);
                Fault(operation);
                return new(ApprovedActionClaimOutcome.Faulted, null, ActionSafetyReason.ProgrammingFault);
            }).ConfigureAwait(false);
        }
        finally
        {
            await inbox.CommandAsync(() =>
            {
                safetyRefreshes.Remove(attempt.AttemptId);
                actionEffects.Remove(attempt.Settled.Task);
                attempt.Settled.TrySetResult();
                return true;
            }).ConfigureAwait(false);
        }
    }

    private async Task<ImmutableArray<OverrideReadResult>> ReadActionEvidenceAsync(
        ImmutableArray<DeliberateOverrideEvidence> expected, CancellationToken token)
    {
        var reads = ImmutableArray.CreateBuilder<OverrideReadResult>();
        foreach (var evidence in expected)
        {
            try { reads.Add(await evidenceStore.ReadAsync(evidence.EvidenceId, token).ConfigureAwait(false)); }
            catch (Exception) { reads.Add(new(OverrideReadOutcome.Unavailable, null)); }
        }
        return reads.ToImmutable();
    }

    private ApprovedActionClaimResult? ActionRejection(Operation? operation, ApprovedActionRequest request)
    {
        if (disposed || operation is null || !ReferenceEquals(active, operation) || operation.Invalidated)
            return ClaimFailure(ApprovedActionClaimOutcome.Stale, ActionSafetyReason.StalePin);
        if (current.Status == ReviewStatus.Faulted)
            return ClaimFailure(ApprovedActionClaimOutcome.Faulted, ActionSafetyReason.ProgrammingFault);
        return operation.Store.MatchesApprovedAction(request) ? null
            : ClaimFailure(ApprovedActionClaimOutcome.Stale, ActionSafetyReason.StalePin);
    }

    private ApprovedActionClaimResult ProbeFailure(Operation operation, ActionSafetyReason reason)
    {
        if (reason == ActionSafetyReason.ProgrammingFault)
        {
            Fault(operation);
            return ClaimFailure(ApprovedActionClaimOutcome.Faulted, reason);
        }
        return ClaimFailure(ApprovedActionClaimOutcome.UnsafeCurrentFacts, reason);
    }

    private static ApprovedActionClaimResult ClaimFailure(ApprovedActionClaimOutcome outcome, ActionSafetyReason reason) => new(outcome, null, reason);

    private static bool ValidPrecedingOutcomes(ApprovedActionRequest request, ApprovedActionItem item,
        ImmutableArray<PriorActionOutcome> outcomes)
    {
        if (outcomes.IsDefault) return false;
        var index = request.Items.IndexOf(item);
        if (index < 0 || outcomes.Length != index) return false;
        for (var position = 0; position < index; position++)
            if (outcomes[position] is not { } outcome || !Enum.IsDefined(outcome.Disposition)
                || outcome.GroupId != request.Items[position].GroupId
                || outcome.OccurrenceId != request.Items[position].Occurrence.OccurrenceId) return false;
        return true;
    }

    private static ActionSafetyInput SafetyInput(ApprovedActionRequest request, CandidateGroup group,
        ActionGroupProbeResult probe, ImmutableArray<PriorActionOutcome> outcomes)
    {
        var dependent = outcomes.Where(outcome => outcome.GroupId == group.GroupId).ToArray();
        var completed = dependent.Where(outcome => outcome.Disposition != PriorActionDisposition.Pending)
            .Select(outcome => outcome.OccurrenceId).ToImmutableHashSet();
        var approved = request.Items.Where(item => item.GroupId == group.GroupId)
            .Select(item => item.Occurrence.OccurrenceId).ToImmutableHashSet();
        var occurrences = probe.Observations.Select(observation =>
        {
            var original = observation.ApprovedOccurrence;
            var opened = observation.Opened.Allocation;
            var final = observation.Final.Allocation;
            var allocation = new AllocationEvidence(final.AllocatedBytes, final.LinkCount,
                opened.Stable && final.Stable && opened.AllocatedBytes == final.AllocatedBytes && opened.LinkCount == final.LinkCount);
            return new ScanFileOccurrence(original.Token, original.DiscoveredPath, original.ResolvedPath,
                original.PhysicalFileKey, original.Enumerate, original.Exact, original.Content, allocation);
        }).ToImmutableArray();
        var overridden = request.Items.Where(item => item.GroupId == group.GroupId && item.OverrideEvidenceId is not null)
            .Select(item => item.Occurrence.OccurrenceId).ToImmutableHashSet();
        return new(group, occurrences, occurrences.Select(occurrence => occurrence.Token.OccurrenceId).ToImmutableHashSet(),
            approved.Except(completed), overridden, overridden.Count > 0,
            dependent.Any(outcome => outcome.Disposition is PriorActionDisposition.Unknown or PriorActionDisposition.Pending));
    }

    private sealed record ClaimStart(Task<ApprovedActionClaimResult> Completion, Operation? Operation, ActionClaimAttempt? Attempt);
    private sealed record RefreshStart(ActionSafetyRefreshResult? Result, Operation? Operation, ActionSafetyRefreshAttempt? Attempt);
}
