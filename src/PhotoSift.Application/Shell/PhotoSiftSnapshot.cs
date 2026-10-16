using System.Collections.Immutable;
using PhotoSift.Application.Action;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public sealed record PhotoSiftSnapshot(
    ShellSnapshot Scope,
    ExactRuleConfirmation? Rules,
    ScanSnapshot Scan,
    WorkflowPhase CurrentPhase,
    ImmutableArray<WorkflowPhaseState> Phases)
{
    public ScanOptions Options { get; init; } = ScanOptions.Default;
    public ReviewSnapshot Review { get; init; } = ReviewSnapshot.Closed;
    public ActionSnapshot Action { get; init; } = ActionSnapshot.Idle;
    public ActionRecoverySnapshot ActionRecovery { get; init; } = ActionRecoverySnapshot.NotLoaded;
    // Historical disclosure for Action.RequestId. Never used as live Review
    // authority, Selection input, a claim or an approval command source.
    public ReviewSnapshot ActionReview { get; init; } = ReviewSnapshot.Closed;
}
