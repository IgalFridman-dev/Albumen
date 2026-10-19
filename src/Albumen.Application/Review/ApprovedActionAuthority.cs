using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Albumen.Application.Scan;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;

namespace Albumen.Application.Review;

// This is part of the Review owner. It reserves Review authority only; Action
// execution, journal transitions, and item state belong to the Action owner.
public sealed partial class ReviewCoordinator
{
    private readonly ProtectedActionSafetyValidator? actionSafety;
    private readonly Dictionary<Task, ActionEffect> actionEffects = [];
    private readonly Queue<ActionEffect> actionCancellations = [];
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
            var effect = new ActionEffect(operation);
            actionEffects.Add(attempt.Completion.Task, effect);
            return new ClaimStart(attempt.Completion.Task, operation, attempt, effect);
        }).ConfigureAwait(false);
        if (start.Operation is { } operation && start.Attempt is { } attempt)
            _ = Task.Run(() => RunClaimAsync(operation, attempt, start.Effect!, cancellationToken), CancellationToken.None);
        return await start.Completion.ConfigureAwait(false);
    }

    private async Task RunClaimAsync(Operation operation, ActionClaimAttempt attempt, ActionEffect effect, CancellationToken caller)
    {
        ApprovedActionClaimResult result;
        try
        {
            result = await RunActionEffectAsync(effect, async token =>
            {
                var reads = await ReadActionEvidenceAsync(attempt.Reads.Select(read => read.Evidence!).ToImmutableArray(), token).ConfigureAwait(false);
                if (actionSafety is null)
                    return await inbox.ResultAsync(() => ActionRejection(operation, attempt.Request)
                        ?? ClaimFailure(ApprovedActionClaimOutcome.UnsafeCurrentFacts, ActionSafetyReason.Unavailable)).ConfigureAwait(false);
                return await actionSafety.ProbeAsync(attempt.Context, attempt.Groups, [],
                    (groups, reason) => inbox.ResultAsync(() => CompleteActionClaim(operation, attempt, reads, groups, reason)), token).ConfigureAwait(false);
            }, caller).ConfigureAwait(false);
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
            var groups = operation.Store.CaptureActionGroups();
            if (!groups.Any(value => value.GroupId == item.GroupId))
                return new RefreshStart(new(ApprovedActionClaimOutcome.Stale, null,
                    ActionSafetyReason.StalePin), null, null);
            var attempt = new ActionSafetyRefreshAttempt(Guid.NewGuid(), accepted, canonicalItem,
                new(accepted.Request.Scan, accepted.Request.Scope), groups, precedingOutcomes,
                operation.Store.ActionEvidence, new(TaskCreationOptions.RunContinuationsAsynchronously));
            safetyRefreshes.Add(attempt.AttemptId);
            var effect = new ActionEffect(operation);
            actionEffects.Add(attempt.Settled.Task, effect);
            return new RefreshStart(null, operation, attempt, effect);
        }).ConfigureAwait(false);
        if (start.Attempt is not { } attempt || start.Operation is not { } operation) return start.Result!;
        // Synchronous opens/analyzer entry points must never run on an owner or
        // observer callback, including when every await completes immediately.
        return await Task.Run(() => RunSafetyRefreshAsync(operation, attempt, start.Effect!, cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ActionSafetyRefreshResult> RunSafetyRefreshAsync(Operation operation,
        ActionSafetyRefreshAttempt attempt, ActionEffect effect, CancellationToken caller)
    {
        try
        {
            return await RunActionEffectAsync(effect, async token =>
            {
                var reads = await ReadActionEvidenceAsync(attempt.ExpectedEvidence, token).ConfigureAwait(false);
                var recycled = attempt.PrecedingOutcomes.Where(outcome =>
                        outcome.Disposition == PriorActionDisposition.Recycled)
                    .Select(outcome => outcome.OccurrenceId).ToImmutableHashSet();
                if (actionSafety is null)
                    return new ActionSafetyRefreshResult(ApprovedActionClaimOutcome.UnsafeCurrentFacts, null, ActionSafetyReason.Unavailable);
                return await actionSafety.ProbeAsync(attempt.Context, attempt.Groups, recycled,
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
                        ActionSafetyInput? input = null;
                        ActionSafetyDecision? decision = null;
                        ActionGroupProbeResult? probe = null;
                        foreach (var candidate in attempt.Groups)
                        {
                            var candidateProbe = groups.Single(value =>
                                value.GroupId == candidate.GroupId);
                            var candidateInput = SafetyInput(attempt.Claim.Request,
                                candidate, candidateProbe, attempt.PrecedingOutcomes);
                            var candidateDecision = ActionSafetyPolicy.Evaluate(candidateInput);
                            if (candidateDecision.Kind != ActionSafetyDecisionKind.Allowed)
                                return new(ApprovedActionClaimOutcome.UnsafeCurrentFacts,
                                    null, candidateDecision.Reason);
                            if (candidate.GroupId == attempt.Item.GroupId)
                            {
                                input = candidateInput;
                                decision = candidateDecision;
                                probe = candidateProbe;
                            }
                        }
                        if (input is null || decision is null || probe is null)
                            return new(ApprovedActionClaimOutcome.Stale, null,
                                ActionSafetyReason.StalePin);
                        return new(ApprovedActionClaimOutcome.Claimed,
                            new(attempt.Claim, attempt.Item, input, decision,
                                probe.Observations, attempt.PrecedingOutcomes),
                            ActionSafetyReason.None);
                    }), token).ConfigureAwait(false);
            }, caller).ConfigureAwait(false);
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
        var memberIds = group.Members.Select(member => member.Token.OccurrenceId)
            .ToImmutableHashSet();
        var dependent = outcomes.Where(outcome => memberIds.Contains(outcome.OccurrenceId)).ToArray();
        var completed = dependent.Where(outcome => outcome.Disposition != PriorActionDisposition.Pending)
            .Select(outcome => outcome.OccurrenceId).ToImmutableHashSet();
        var approved = request.SelectedOccurrenceIds.Where(memberIds.Contains).ToImmutableHashSet();
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

    // Owner turns only enqueue immutable effect references. The after-turn
    // delivery schedules callbacks; no cancellation callback runs on the owner.
    private void QueueActionCancellation(Operation operation)
    {
        foreach (var effect in actionEffects.Values)
            if (ReferenceEquals(effect.Operation, operation)) actionCancellations.Enqueue(effect);
    }

    private void DeliverActionCancellations()
    {
        while (actionCancellations.TryDequeue(out var effect)) _ = effect.RequestCancellation();
    }

    private static async Task<T> RunActionEffectAsync<T>(ActionEffect effect,
        Func<CancellationToken, Task<T>> work, CancellationToken caller)
    {
        var registration = caller.Register(() => _ = effect.RequestCancellation());
        ExceptionDispatchInfo? fault = null;
        T result = default!;
        try { result = await work(effect.Token).ConfigureAwait(false); }
        catch (Exception exception) { fault = ExceptionDispatchInfo.Capture(exception); }
        try
        {
            // The caller callback schedules onto the effect's tracked lane.
            // Unregister it before closing that lane, then drain all callbacks.
            await registration.DisposeAsync().ConfigureAwait(false);
            await effect.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFault) when (fault is not null)
        {
            throw new AggregateException(fault.SourceException, cleanupFault);
        }
        fault?.Throw();
        return result;
    }

    // This lock serializes cancellation-task/disposal identity only. Review
    // authority and the registry remain exclusively owned by inbox turns.
    private sealed class ActionEffect(Operation operation) : IAsyncDisposable
    {
        private readonly object lifecycle = new();
        private readonly CancellationTokenSource cancellation = new();
        private Task? cancelTask;
        private bool closed;
        internal Operation Operation { get; } = operation;
        internal CancellationToken Token => cancellation.Token;

        internal Task RequestCancellation()
        {
            lock (lifecycle)
                return cancelTask ??= closed ? Task.CompletedTask
                    : Task.Run(() => cancellation.CancelAsync(), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Task pending;
            lock (lifecycle)
            {
                closed = true;
                pending = cancelTask ?? Task.CompletedTask;
            }
            try { await pending.ConfigureAwait(false); }
            finally { cancellation.Dispose(); }
            GC.SuppressFinalize(this);
        }
    }

    private sealed record ClaimStart(Task<ApprovedActionClaimResult> Completion, Operation? Operation,
        ActionClaimAttempt? Attempt, ActionEffect? Effect = null);
    private sealed record RefreshStart(ActionSafetyRefreshResult? Result, Operation? Operation,
        ActionSafetyRefreshAttempt? Attempt, ActionEffect? Effect = null);
}
