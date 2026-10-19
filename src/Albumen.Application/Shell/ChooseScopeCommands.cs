namespace Albumen.Application.Shell;

public sealed record ChooseScopeCommand;

public sealed record ChangeScopeRecursionCommand(bool IncludeSubfolders);

public sealed record RetryScopePreflightCommand;
