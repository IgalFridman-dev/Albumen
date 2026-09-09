using System.Collections.Immutable;
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
    public ReviewSnapshot Review { get; init; } = ReviewSnapshot.Closed;
}
