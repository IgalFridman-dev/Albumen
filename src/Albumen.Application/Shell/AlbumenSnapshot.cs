using System.Collections.Immutable;
using Albumen.Application.Action;
using Albumen.Application.Scan;
using Albumen.Application.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Workflow;

namespace Albumen.Application.Shell;

public sealed record AlbumenSnapshot(
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
    // True while the Scope is checking the folder again because the finished
    // cleanup removed photos from it. Presentation only; cleared when the check ends.
    public bool IsRecheckingScopeAfterCleanup { get; init; }
}
