using System.Collections.Immutable;
using Albumen.Domain.Scan;

namespace Albumen.Domain.Review;

public sealed record ReviewSessionPin(Guid ReviewSessionId, ScanPin Scan);

public sealed record ReviewCommandPin(
    ReviewSessionPin Session,
    Guid GroupId,
    long GroupRevision,
    long SelectionRevision);

public enum SelectionCommandKind
{
    Toggle,
    ReplaceFocusedGroup,
}

public sealed record SelectionCommand(
    ReviewCommandPin Pin,
    SelectionCommandKind Kind,
    ImmutableArray<long> OccurrenceIds,
    Guid CommandId);

public enum SurvivorDecisionKind
{
    Allowed,
    OverrideRequired,
    IneligibleSelection,
    InconsistentEvidence,
}

public enum SurvivorUnavailableReason
{
    None,
    NoProvenSurvivor,
    IneligibleOccurrence,
    UnknownLinks,
    InconsistentFacts,
}

public sealed record ExternalSurvivor(
    PhysicalFileKey PhysicalFileKey,
    uint LinkCount,
    int KnownPathCount);

public sealed record SurvivorDecision(
    SurvivorDecisionKind Kind,
    SurvivorUnavailableReason Reason,
    int RemainingKnownPathOccurrences,
    int RemainingPhysicalKeys,
    ImmutableArray<ExternalSurvivor> ExternalSurvivors);

public enum SelectionRecoveryUnavailableReason
{
    None,
    UnknownAllocation,
    UnknownLinks,
    InconsistentFacts,
    Overflow,
}

public sealed record SelectionRecovery(
    long? Bytes,
    SelectionRecoveryUnavailableReason Reason);

public sealed record SelectionEvaluation(
    SurvivorDecision Survivor,
    SelectionRecovery Recovery);

public readonly record struct OverrideEvidenceId(Guid Value);

public sealed record OverrideProposalToken(
    Guid ProposalId,
    ReviewCommandPin Pin,
    ImmutableArray<long> ProposedSelectedOccurrenceIds,
    string SurvivorPolicyVersion,
    string ConsequenceTextVersion);

public sealed record OverrideAffirmationCommand(
    OverrideProposalToken Proposal,
    OverrideEvidenceId EvidenceId,
    DateTimeOffset AffirmedUtc);

public sealed record AffectedPhysicalRevision(
    PhysicalFileKey PhysicalFileKey,
    ContentRevision Content,
    AllocationEvidence Allocation);

public sealed record DeliberateOverrideEvidence(
    OverrideEvidenceId EvidenceId,
    DateTimeOffset AffirmedUtc,
    ReviewCommandPin Pin,
    ImmutableArray<long> SelectedOccurrenceIds,
    ImmutableArray<AffectedPhysicalRevision> AffectedPhysicalRevisions,
    string SurvivorPolicyVersion,
    string ConsequenceTextVersion,
    bool DeliberatelyAffirmed);
