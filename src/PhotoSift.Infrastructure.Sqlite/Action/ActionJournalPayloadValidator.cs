using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Infrastructure.Sqlite.Action;

// JSON annotations do not validate collection element nullability. Walk the
// complete reference graph before encoding, and again before using a decoded
// row as a predecessor. Optional fields retain their Domain null semantics.
internal static class ActionJournalPayloadValidator
{
    internal static bool IsComplete(ActionRequestedFact? fact) => fact is not null
        && fact.Safety is { } safety && safety.Claim is { } claim && Request(claim.Request)
        && Item(safety.Item) && safety.Input is { } input && Group(input.ApprovedGroup)
        && !input.CurrentOccurrences.IsDefault && input.CurrentOccurrences.All(Occurrence)
        && input.FreshEligibleOccurrenceIds is not null && input.RemainingApprovedOccurrenceIds is not null
        && input.OverrideApprovedOccurrenceIds is not null
        && safety.Decision is { } decision && Enum.IsDefined(decision.Kind) && Enum.IsDefined(decision.Reason)
        && Survivor(decision.Survivor)
        && !safety.Observations.IsDefault && safety.Observations.All(value => value is not null
            && Occurrence(value.ApprovedOccurrence) && Protected(value.Opened) && Protected(value.Final)
            && value.Digest is { } digest && digest.ByteLength >= 0 && Digest(digest.Sha256))
        && !safety.PrecedingOutcomes.IsDefault && safety.PrecedingOutcomes.All(value => value is not null
            && Enum.IsDefined(value.Disposition))
        && (fact.OverrideEvidence is null || Override(fact.OverrideEvidence))
        && Digest(fact.RequestPayloadSha256);

    private static bool Request(ApprovedActionRequest? value) => value is not null && Scan(value.Scan)
        && value.Scope is { Selection: { } selection, RootIdentity: not null } scope
        && Text(selection.SelectedPath) && Enum.IsDefined(selection.Recursion) && Text(scope.ResolvedPath)
        && !value.Items.IsDefaultOrEmpty && value.Items.All(Item) && !value.SelectedOccurrenceIds.IsDefault
        && value.Disclosure is { Unresolved: not null, Recovery: { } recovery } disclosure
        && Enum.IsDefined(disclosure.Coverage) && Enum.IsDefined(recovery.Reason)
        && Text(disclosure.FutureDestination) && Text(disclosure.RevalidationNotice);

    private static bool Item(ApprovedActionItem? value) => value is not null && Token(value.Occurrence)
        && Text(value.DiscoveredPath) && Text(value.ResolvedPath) && Content(value.Content) && Allocation(value.Allocation);

    private static bool Group(CandidateGroup? value) => value is not null && Scan(value.Pin)
        && Enum.IsDefined(value.Category) && !value.Members.IsDefault && value.Members.All(Occurrence)
        && !value.Equality.IsDefault && value.Equality.All(pair => pair is not null
            && Token(pair.Left) && Token(pair.Right) && Content(pair.LeftContent) && Content(pair.RightContent))
        && value.PotentialRecovery is { } recovery && Enum.IsDefined(recovery.Reason);

    private static bool Occurrence(ScanFileOccurrence? value) => value is not null && Token(value.Token)
        && Text(value.DiscoveredPath) && Text(value.ResolvedPath) && Stage(value.Enumerate) && Stage(value.Exact)
        && (value.Content is null || Content(value.Content)) && Allocation(value.Allocation);

    private static bool Survivor(SurvivorDecision? value) => value is not null && Enum.IsDefined(value.Kind)
        && Enum.IsDefined(value.Reason) && !value.ExternalSurvivors.IsDefault
        && value.ExternalSurvivors.All(external => external is not null);

    private static bool Protected(ProtectedFileFacts? value) => value is not null && value.ByteLength >= 0
        && Text(value.ResolvedPath) && Allocation(value.Allocation);

    private static bool Override(DeliberateOverrideEvidence? value) => value is not null
        && value.Pin is { Session: { } session } && Scan(session.Scan)
        && !value.SelectedOccurrenceIds.IsDefault && !value.AffectedPhysicalRevisions.IsDefault
        && value.AffectedPhysicalRevisions.All(affected => affected is not null
            && Content(affected.Content) && Allocation(affected.Allocation))
        && Text(value.SurvivorPolicyVersion) && Text(value.ConsequenceTextVersion);

    private static bool Token(OccurrenceToken? value) => value is not null && Scan(value.Pin);
    private static bool Scan(ScanPin? value) => value is not null;
    private static bool Allocation(AllocationEvidence? value) => value is not null;
    private static bool Stage(StageOutcome? value) => value is not null && Enum.IsDefined(value.State) && Enum.IsDefined(value.Reason);
    private static bool Content(ContentRevision? value) => value is not null && value.ByteLength >= 0 && Digest(value.Sha256);
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
}
