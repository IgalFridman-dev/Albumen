using System.Collections.Immutable;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public sealed record ShellSnapshot(
    ImmutableArray<WorkflowPhaseState> Phases,
    string Instruction);
