namespace PhotoSift.Domain.Scope;

public enum ScopeRecursion
{
    TopLevelOnly,
    IncludeSubfolders,
}

public sealed record ScopeSelection
{
    public ScopeSelection(string selectedPath, ScopeRecursion recursion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        if (!Path.IsPathFullyQualified(selectedPath))
        {
            throw new ArgumentException("Scope path must be fully qualified.", nameof(selectedPath));
        }

        SelectedPath = selectedPath;
        Recursion = recursion;
    }

    public string SelectedPath { get; }

    public ScopeRecursion Recursion { get; }
}
