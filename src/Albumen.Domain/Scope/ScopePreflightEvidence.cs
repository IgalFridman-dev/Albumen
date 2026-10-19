namespace Albumen.Domain.Scope;

public enum ScopeVolumeKind
{
    Fixed,
    Removable,
    Remote,
    Unsupported,
    Unavailable,
    Unknown,
}

public enum ScopeCloudState
{
    Local,
    Synchronized,
    Placeholder,
    Unknown,
}

public enum ScopeTraversalState
{
    NotRun,
    Complete,
    RootAccessDenied,
    DescendantAccessDenied,
    Unknown,
}

public enum SupportedImageFormat
{
    Jpeg,
    Png,
    Heic,
}

public sealed record ScopeOutcomeCounts
{
    public ScopeOutcomeCounts(
        int supported,
        int unsupportedExtension,
        int unreadable,
        int codecUnavailable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(supported);
        ArgumentOutOfRangeException.ThrowIfNegative(unsupportedExtension);
        ArgumentOutOfRangeException.ThrowIfNegative(unreadable);
        ArgumentOutOfRangeException.ThrowIfNegative(codecUnavailable);

        Supported = supported;
        UnsupportedExtension = unsupportedExtension;
        Unreadable = unreadable;
        CodecUnavailable = codecUnavailable;
    }

    public int Supported { get; }

    public int UnsupportedExtension { get; }

    public int Unreadable { get; }

    public int CodecUnavailable { get; }
}

public sealed record ScopeRestrictionCounts
{
    public ScopeRestrictionCounts(
        int escapingReparseTargets,
        int duplicateDirectoryIdentities)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(escapingReparseTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateDirectoryIdentities);

        EscapingReparseTargets = escapingReparseTargets;
        DuplicateDirectoryIdentities = duplicateDirectoryIdentities;
    }

    public int EscapingReparseTargets { get; }

    public int DuplicateDirectoryIdentities { get; }
}

public sealed record ScopePreflightEvidence(
    ResolvedScope? Scope,
    ScopeVolumeKind Volume,
    ScopeCloudState Cloud,
    ScopeTraversalState Traversal,
    ScopeOutcomeCounts Counts,
    ScopeRestrictionCounts Restrictions);
