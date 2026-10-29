using System.Collections.Immutable;

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
    // The folder card names a few photos so the user can tell it is the right folder.
    public const int MaxSampleSupportedFiles = 6;

    public ScopeOutcomeCounts(
        int supported,
        int unsupportedExtension,
        int unreadable,
        int codecUnavailable,
        ImmutableArray<string> sampleSupportedFiles = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(supported);
        ArgumentOutOfRangeException.ThrowIfNegative(unsupportedExtension);
        ArgumentOutOfRangeException.ThrowIfNegative(unreadable);
        ArgumentOutOfRangeException.ThrowIfNegative(codecUnavailable);
        var samples = sampleSupportedFiles.IsDefault ? ImmutableArray<string>.Empty : sampleSupportedFiles;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            samples.Length, MaxSampleSupportedFiles, nameof(sampleSupportedFiles));

        Supported = supported;
        UnsupportedExtension = unsupportedExtension;
        Unreadable = unreadable;
        CodecUnavailable = codecUnavailable;
        SampleSupportedFiles = samples;
    }

    public int Supported { get; }

    public int UnsupportedExtension { get; }

    public int Unreadable { get; }

    public int CodecUnavailable { get; }

    // The first supported file names in traversal order, at most six.
    public ImmutableArray<string> SampleSupportedFiles { get; }

    public bool Equals(ScopeOutcomeCounts? other) =>
        other is not null
        && Supported == other.Supported
        && UnsupportedExtension == other.UnsupportedExtension
        && Unreadable == other.Unreadable
        && CodecUnavailable == other.CodecUnavailable
        && SampleSupportedFiles.SequenceEqual(other.SampleSupportedFiles, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Supported, UnsupportedExtension, Unreadable, CodecUnavailable, SampleSupportedFiles.Length);
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
