using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Shell;

public static class ShellPhasePolicy
{
    public static bool CanStart(ShellSnapshot scope, ExactRuleConfirmation? rules) =>
        scope.ScopeState == ScopeSessionState.Ready
        && rules is not null
        && rules.ScopeRevision == scope.Revision;
}
