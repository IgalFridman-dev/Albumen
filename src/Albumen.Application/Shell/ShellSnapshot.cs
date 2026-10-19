using System.Collections.Immutable;
using Albumen.Domain.Scope;
using Albumen.Domain.Workflow;

namespace Albumen.Application.Shell;

public sealed record ShellSnapshot(
    ImmutableArray<WorkflowPhaseState> Phases,
    string Instruction,
    ScopeSessionState ScopeState = ScopeSessionState.Cold,
    ScopeSelection? Selection = null,
    ScopePreflightResult? Preflight = null,
    string RuleSetStatus = "Not configured",
    long Revision = 0);
