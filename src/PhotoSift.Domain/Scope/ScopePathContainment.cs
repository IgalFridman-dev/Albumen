namespace PhotoSift.Domain.Scope;

public static class ScopePathContainment
{
    public static bool Contains(string resolvedRoot, string resolvedCandidate)
    {
        ArgumentNullException.ThrowIfNull(resolvedRoot);
        ArgumentNullException.ThrowIfNull(resolvedCandidate);

        var canonicalRoot = Canonicalize(resolvedRoot);
        var canonicalCandidate = Canonicalize(resolvedCandidate);

        if (string.Equals(canonicalRoot, canonicalCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var descendantPrefix = canonicalRoot.EndsWith('\\')
            ? canonicalRoot
            : canonicalRoot + '\\';
        return canonicalCandidate.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonicalize(string path)
    {
        var canonicalPath = path.Replace('/', '\\');
        if (canonicalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            canonicalPath = @"\\" + canonicalPath[8..];
        }
        else if (canonicalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            canonicalPath = canonicalPath[4..];
        }

        while (canonicalPath.Length > 3 && canonicalPath.EndsWith('\\'))
        {
            canonicalPath = canonicalPath[..^1];
        }

        return canonicalPath;
    }
}
