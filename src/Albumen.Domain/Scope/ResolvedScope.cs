namespace Albumen.Domain.Scope;

public sealed record ResolvedScope(
    ScopeSelection Selection,
    string ResolvedPath,
    DirectoryIdentity RootIdentity);
