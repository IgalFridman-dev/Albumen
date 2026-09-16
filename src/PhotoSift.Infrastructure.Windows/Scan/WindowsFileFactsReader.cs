using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scan;

internal sealed class WindowsFileFactsReader(IWindowsFileFactsSource source)
{
    private const uint ReparsePoint = 0x400;
    private const uint DirectoryAttribute = 0x10;

    internal WindowsScopeReadGuard ValidateScope(ProtectedOpenRequest request,
        ImmutableDictionary<string, WindowsDirectoryProof>? openedChain, CancellationToken cancellationToken,
        bool retainDirectoryGuards = false)
    {
        var scope = request.Context.Scope;
        var selectedRoot = NormalizePath(scope.Selection.SelectedPath);
        var resolvedRoot = NormalizePath(scope.ResolvedPath);
        var discovered = NormalizePath(request.DiscoveredPath);
        var resolved = NormalizePath(request.ResolvedPath);
        Require(ScopePathContainment.Contains(selectedRoot, discovered)
            && ScopePathContainment.Contains(resolvedRoot, resolved), ScanReason.UnsafeLocation);
        if (scope.Selection.Recursion is ScopeRecursion.TopLevelOnly)
        {
            Require(SamePath(Path.GetDirectoryName(discovered), selectedRoot)
                && SamePath(Path.GetDirectoryName(resolved), resolvedRoot), ScanReason.UnsafeLocation);
        }

        Require(SamePath(Path.GetFileName(discovered), Path.GetFileName(resolved)), ScanReason.Changed);
        return ValidateDirectory(scope, Path.GetDirectoryName(discovered)!, Path.GetDirectoryName(resolved)!,
            expectedIdentity: null, openedChain, cancellationToken, retainDirectoryGuards);
    }

    internal WindowsScopeReadGuard ValidateDirectory(ResolvedScope scope, string discoveredDirectory,
        string resolvedDirectory, DirectoryIdentity? expectedIdentity,
        ImmutableDictionary<string, WindowsDirectoryProof>? openedChain, CancellationToken cancellationToken,
        bool retainDirectoryGuards = false)
    {
        var selectedRoot = NormalizePath(scope.Selection.SelectedPath);
        var resolvedRoot = NormalizePath(scope.ResolvedPath);
        discoveredDirectory = NormalizePath(discoveredDirectory);
        resolvedDirectory = NormalizePath(resolvedDirectory);
        Require(ScopePathContainment.Contains(selectedRoot, discoveredDirectory)
            && ScopePathContainment.Contains(resolvedRoot, resolvedDirectory), ScanReason.UnsafeLocation);

        var guard = new WindowsScopeReadGuard();
        try
        {
            var directories = new Dictionary<string, WindowsFileObservation>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = InspectChain(selectedRoot);
            var expectedRoot = new PhysicalFileKey(scope.RootIdentity.VolumeSerialNumber,
                new FileId128(scope.RootIdentity.FileIdHigh, scope.RootIdentity.FileIdLow));
            Require(selected.PhysicalFileKey == expectedRoot && SamePath(selected.ResolvedPath, resolvedRoot),
                ScanReason.Changed);
            var root = InspectChain(resolvedRoot);
            Require(root.PhysicalFileKey == expectedRoot && SamePath(root.ResolvedPath, resolvedRoot),
                ScanReason.Changed);
            var discoveredParent = InspectChain(discoveredDirectory);
            var resolvedParent = InspectChain(resolvedDirectory);
            Require(SamePath(discoveredParent.ResolvedPath, resolvedDirectory)
                && SamePath(resolvedParent.ResolvedPath, resolvedDirectory), ScanReason.Changed);
            if (expectedIdentity is { } identity)
            {
                var expectedKey = new PhysicalFileKey(identity.VolumeSerialNumber,
                    new FileId128(identity.FileIdHigh, identity.FileIdLow));
                Require(discoveredParent.PhysicalFileKey == expectedKey && resolvedParent.PhysicalFileKey == expectedKey,
                    ScanReason.Changed);
            }
            if (openedChain is not null)
            {
                Require(guard.Proofs.Count == openedChain.Count && guard.Proofs.All(pair =>
                    openedChain.TryGetValue(pair.Key, out var previous) && pair.Value == previous), ScanReason.Changed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return guard;

            WindowsFileObservation InspectChain(string directory)
            {
                directory = NormalizePath(directory);
                if (directories.TryGetValue(directory, out var known))
                {
                    return known;
                }

                Require(visiting.Add(directory), ScanReason.UnknownSafety);
                var parent = Path.GetDirectoryName(directory);
                if (!string.IsNullOrEmpty(parent))
                {
                    InspectChain(parent);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var raw = ReadDirectory(directory, follow: false);
                var target = raw;
                if ((raw.Attributes!.Value & ReparsePoint) != 0)
                {
                    // Only the existing contained directory-link policy is supported.
                    Require(raw.ReparseTag is 0xA0000003u or 0xA000000Cu, ScanReason.UnsafeLocation);
                    target = ReadDirectory(directory, follow: true);
                    Require((target.Attributes!.Value & ReparsePoint) == 0, ScanReason.UnsafeLocation);
                }

                if (ScopePathContainment.Contains(selectedRoot, directory)
                    || ScopePathContainment.Contains(resolvedRoot, directory))
                {
                    Require(ScopePathContainment.Contains(resolvedRoot, target.ResolvedPath!), ScanReason.UnsafeLocation);
                }

                directories.Add(directory, target);
                if (!SamePath(directory, target.ResolvedPath))
                {
                    var resolvedTarget = InspectChain(target.ResolvedPath!);
                    Require(resolvedTarget.PhysicalFileKey == target.PhysicalFileKey, ScanReason.Changed);
                }

                visiting.Remove(directory);
                return target;
            }

            WindowsFileObservation ReadDirectory(string path, bool follow)
            {
                var handle = expectedIdentity is not null || retainDirectoryGuards
                    ? WindowsFileNative.OpenDirectoryGuard(path, follow)
                    : WindowsFileNative.OpenMetadata(path, follow);
                guard.Handles.Add(handle);
                var facts = source.Read(handle, path);
                ValidateSafety(facts, directory: true);
                var proofPath = path + (follow ? "|target" : "|entry");
                guard.Proofs.Add(proofPath, new WindowsDirectoryProof(facts.PhysicalFileKey!.Value,
                    NormalizePath(facts.ResolvedPath!), facts.Attributes!.Value & ReparsePoint, facts.ReparseTag));
                return facts;
            }
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    internal ProtectedFileFacts ReadContent(SafeFileHandle handle, ProtectedOpenRequest request,
        ProtectedFileFacts? opened = null)
    {
        var facts = source.Read(handle, NormalizePath(request.ResolvedPath));
        ValidateSafety(facts, directory: false);
        var resolved = NormalizePath(facts.ResolvedPath!);
        Require(ScopePathContainment.Contains(NormalizePath(request.Context.Scope.ResolvedPath), resolved),
            ScanReason.UnsafeLocation);
        Require(SamePath(resolved, NormalizePath(request.ResolvedPath))
            && facts.PhysicalFileKey == request.ExpectedPhysicalFileKey, ScanReason.Changed);
        Require(facts.ByteLength.HasValue && facts.ByteLength.Value >= 0, ScanReason.UnknownSafety);
        if (opened is not null)
        {
            Require(facts.PhysicalFileKey == opened.PhysicalFileKey && facts.ByteLength == opened.ByteLength,
                ScanReason.Changed);
        }

        var allocation = facts.Allocation;
        var stable = allocation.Stable && allocation.AllocatedBytes is >= 0 && allocation.LinkCount is > 0;
        if (opened is not null)
        {
            stable &= opened.Allocation.Stable
                && allocation.AllocatedBytes == opened.Allocation.AllocatedBytes
                && allocation.LinkCount == opened.Allocation.LinkCount;
        }

        return new ProtectedFileFacts(facts.PhysicalFileKey!.Value, facts.ByteLength!.Value,
            resolved, facts.LastWriteUtc, allocation with { Stable = stable });
    }

    internal void ValidateDirectoryHandle(SafeFileHandle handle,
        PhotoSift.Infrastructure.Windows.Scope.WindowsDirectoryFacts expected)
    {
        var facts = source.Read(handle, NormalizePath(expected.ResolvedPath));
        ValidateSafety(facts, directory: true);
        var key = new PhysicalFileKey(expected.Identity.VolumeSerialNumber,
            new FileId128(expected.Identity.FileIdHigh, expected.Identity.FileIdLow));
        Require(facts.PhysicalFileKey == key && SamePath(facts.ResolvedPath, NormalizePath(expected.ResolvedPath)), ScanReason.Changed);
        Require((facts.Attributes!.Value & ReparsePoint) == 0, ScanReason.UnsafeLocation);
    }

    private static void ValidateSafety(WindowsFileObservation facts, bool directory)
    {
        Require(facts.PhysicalFileKey.HasValue && facts.Attributes.HasValue && facts.ReparseTag.HasValue
            && facts.DeletePending.HasValue && facts.IsDirectory.HasValue
            && !string.IsNullOrWhiteSpace(facts.ResolvedPath), ScanReason.UnknownSafety);
        Require(facts.CloudState is not ScopeCloudState.Unknown && facts.VolumeKind is not ScopeVolumeKind.Unknown,
            ScanReason.UnknownSafety);
        Require(facts.CloudState is ScopeCloudState.Local
            && facts.VolumeKind is ScopeVolumeKind.Fixed or ScopeVolumeKind.Removable, ScanReason.UnsafeLocation);
        Require(facts.DeletePending is false, ScanReason.Changed);
        Require(facts.IsDirectory == directory && ((facts.Attributes!.Value & DirectoryAttribute) != 0) == directory,
            ScanReason.UnsafeLocation);
        if (!directory)
        {
            Require((facts.Attributes!.Value & ReparsePoint) == 0, ScanReason.UnsafeLocation);
        }

        NormalizePath(facts.ResolvedPath!);
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        // The scope supports local drive paths and the unnamed data stream only.
        Require(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
            && path[2] is '\\' or '/' && !path.AsSpan(2).Contains(':'), ScanReason.UnsafeLocation);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool SamePath(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, ScanReason reason)
    {
        if (!condition)
        {
            throw new ScanReadException(new StageOutcome(reason is ScanReason.Changed
                ? ScanStageState.FailedRetryable : ScanStageState.FailedTerminal, reason),
                $"Protected file validation failed: {reason}.");
        }
    }
}

internal sealed record WindowsDirectoryProof(PhysicalFileKey PhysicalFileKey, string ResolvedPath,
    uint ReparseAttribute, uint? ReparseTag);

internal sealed class WindowsScopeReadGuard : IDisposable
{
    internal List<SafeFileHandle> Handles { get; } = [];
    internal Dictionary<string, WindowsDirectoryProof> Proofs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        for (var index = Handles.Count - 1; index >= 0; index--)
        {
            Handles[index].Dispose();
        }
    }
}
