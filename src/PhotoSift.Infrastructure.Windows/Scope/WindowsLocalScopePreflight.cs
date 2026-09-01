using System.ComponentModel;
using PhotoSift.Application.Scope;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

public sealed class WindowsLocalScopePreflight : ILocalScopePreflight
{
    private static readonly ScopeOutcomeCounts EmptyCounts = new(0, 0, 0, 0);
    private static readonly ScopeRestrictionCounts EmptyRestrictions = new(0, 0);

    private readonly IWindowsScopeNative native;

    public WindowsLocalScopePreflight()
        : this(new WindowsScopeNative(
            new WicCodecCapability(new ComClassAvailability())))
    {
    }

    internal WindowsLocalScopePreflight(IWindowsScopeNative native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public Task<ScopePreflightEvidence> InspectAsync(
        ScopeSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Task.Run(
            () => Inspect(selection, cancellationToken),
            cancellationToken);
    }

    private ScopePreflightEvidence Inspect(
        ScopeSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WindowsDirectoryFacts root;
        try
        {
            root = native.InspectDirectory(selection.SelectedPath);
        }
        catch (UnauthorizedAccessException)
        {
            return RootFailureEvidence(ScopeTraversalState.RootAccessDenied);
        }
        catch (WindowsScopeNativeException exception)
            when (exception.Kind is WindowsScopeFailureKind.AccessDenied)
        {
            return RootFailureEvidence(ScopeTraversalState.RootAccessDenied);
        }
        catch (WindowsScopeNativeException exception)
            when (exception.Kind is WindowsScopeFailureKind.Unavailable)
        {
            return new ScopePreflightEvidence(
                Scope: null,
                ScopeVolumeKind.Unavailable,
                ScopeCloudState.Local,
                ScopeTraversalState.NotRun,
                EmptyCounts,
                EmptyRestrictions);
        }
        catch (IOException)
        {
            return RootFailureEvidence(ScopeTraversalState.Unknown);
        }
        catch (Win32Exception)
        {
            return RootFailureEvidence(ScopeTraversalState.Unknown);
        }

        var resolvedScope = new ResolvedScope(
            selection,
            root.ResolvedPath,
            root.Identity);

        ScopeVolumeKind volume;
        try
        {
            volume = native.GetVolumeKind(root.ResolvedPath);
        }
        catch (UnauthorizedAccessException)
        {
            return FailureEvidence(
                resolvedScope,
                ScopeVolumeKind.Unknown,
                root.CloudState,
                ScopeTraversalState.RootAccessDenied,
                EmptyRestrictions);
        }
        catch (WindowsScopeNativeException exception)
            when (exception.Kind is WindowsScopeFailureKind.AccessDenied)
        {
            return FailureEvidence(
                resolvedScope,
                ScopeVolumeKind.Unknown,
                root.CloudState,
                ScopeTraversalState.RootAccessDenied,
                EmptyRestrictions);
        }
        catch (WindowsScopeNativeException exception)
            when (exception.Kind is WindowsScopeFailureKind.Unavailable)
        {
            return IncompleteEvidence(
                resolvedScope,
                ScopeVolumeKind.Unavailable,
                root.CloudState);
        }
        catch (IOException)
        {
            return FailureEvidence(
                resolvedScope,
                ScopeVolumeKind.Unknown,
                root.CloudState,
                ScopeTraversalState.Unknown,
                EmptyRestrictions);
        }
        catch (Win32Exception)
        {
            return FailureEvidence(
                resolvedScope,
                ScopeVolumeKind.Unknown,
                root.CloudState,
                ScopeTraversalState.Unknown,
                EmptyRestrictions);
        }

        if (volume is not ScopeVolumeKind.Fixed and not ScopeVolumeKind.Removable)
        {
            return IncompleteEvidence(resolvedScope, volume, root.CloudState);
        }

        if (root.CloudState is not ScopeCloudState.Local)
        {
            return IncompleteEvidence(resolvedScope, volume, root.CloudState);
        }

        var counts = EmptyCounts;
        var restrictions = EmptyRestrictions;
        var codecAvailability = new Dictionary<SupportedImageFormat, bool>();
        var pending = new Stack<WindowsDirectoryFacts>();
        var visited = new HashSet<DirectoryIdentity> { root.Identity };
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var entry in native.EnumerateDirectory(directory.ResolvedPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory &&
                        selection.Recursion is ScopeRecursion.TopLevelOnly)
                    {
                        continue;
                    }

                    if (!entry.IsDirectory)
                    {
                        if (entry.CloudState is not ScopeCloudState.Local)
                        {
                            return IncompleteEvidence(
                                resolvedScope,
                                volume,
                                entry.CloudState);
                        }

                        var fileResult = CountFile(
                            entry.Path,
                            entry.IsReparsePoint,
                            counts,
                            codecAvailability);
                        if (fileResult.CloudState is not ScopeCloudState.Local)
                        {
                            return IncompleteEvidence(
                                resolvedScope,
                                volume,
                                fileResult.CloudState);
                        }

                        counts = fileResult.Counts;
                        continue;
                    }

                    WindowsDirectoryFacts child;
                    try
                    {
                        child = native.InspectDirectory(entry.Path);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return FailureEvidence(
                            resolvedScope,
                            volume,
                            ScopeCloudState.Local,
                            ScopeTraversalState.DescendantAccessDenied,
                            restrictions);
                    }
                    catch (WindowsScopeNativeException exception)
                        when (exception.Kind is WindowsScopeFailureKind.AccessDenied)
                    {
                        return FailureEvidence(
                            resolvedScope,
                            volume,
                            ScopeCloudState.Local,
                            ScopeTraversalState.DescendantAccessDenied,
                            restrictions);
                    }
                    catch (IOException)
                    {
                        return FailureEvidence(
                            resolvedScope,
                            volume,
                            ScopeCloudState.Local,
                            ScopeTraversalState.Unknown,
                            restrictions);
                    }
                    catch (Win32Exception)
                    {
                        return FailureEvidence(
                            resolvedScope,
                            volume,
                            ScopeCloudState.Local,
                            ScopeTraversalState.Unknown,
                            restrictions);
                    }

                    if (!ScopePathContainment.Contains(root.ResolvedPath, child.ResolvedPath))
                    {
                        restrictions = new ScopeRestrictionCounts(
                            restrictions.EscapingReparseTargets + 1,
                            restrictions.DuplicateDirectoryIdentities);
                        continue;
                    }

                    if (entry.CloudState is not ScopeCloudState.Local)
                    {
                        return IncompleteEvidence(resolvedScope, volume, entry.CloudState);
                    }

                    if (child.CloudState is not ScopeCloudState.Local)
                    {
                        return IncompleteEvidence(resolvedScope, volume, child.CloudState);
                    }

                    if (!visited.Add(child.Identity))
                    {
                        restrictions = new ScopeRestrictionCounts(
                            restrictions.EscapingReparseTargets,
                            restrictions.DuplicateDirectoryIdentities + 1);
                        continue;
                    }

                    pending.Push(child);
                }
            }
            catch (UnauthorizedAccessException)
            {
                var traversal = directory.Identity == root.Identity
                    ? ScopeTraversalState.RootAccessDenied
                    : ScopeTraversalState.DescendantAccessDenied;
                return FailureEvidence(
                    resolvedScope,
                    volume,
                    ScopeCloudState.Local,
                    traversal,
                    restrictions);
            }
            catch (WindowsScopeNativeException exception)
                when (exception.Kind is WindowsScopeFailureKind.AccessDenied)
            {
                var traversal = directory.Identity == root.Identity
                    ? ScopeTraversalState.RootAccessDenied
                    : ScopeTraversalState.DescendantAccessDenied;
                return FailureEvidence(
                    resolvedScope,
                    volume,
                    ScopeCloudState.Local,
                    traversal,
                    restrictions);
            }
            catch (IOException)
            {
                return FailureEvidence(
                    resolvedScope,
                    volume,
                    ScopeCloudState.Local,
                    ScopeTraversalState.Unknown,
                    restrictions);
            }
            catch (Win32Exception)
            {
                return FailureEvidence(
                    resolvedScope,
                    volume,
                    ScopeCloudState.Local,
                    ScopeTraversalState.Unknown,
                    restrictions);
            }
        }

        return new ScopePreflightEvidence(
            resolvedScope,
            volume,
            ScopeCloudState.Local,
            ScopeTraversalState.Complete,
            counts,
            restrictions);
    }

    private static ScopePreflightEvidence RootFailureEvidence(
        ScopeTraversalState traversal) =>
        new(
            Scope: null,
            ScopeVolumeKind.Unknown,
            ScopeCloudState.Unknown,
            traversal,
            EmptyCounts,
            EmptyRestrictions);

    private static ScopePreflightEvidence FailureEvidence(
        ResolvedScope scope,
        ScopeVolumeKind volume,
        ScopeCloudState cloud,
        ScopeTraversalState traversal,
        ScopeRestrictionCounts restrictions) =>
        new(
            scope,
            volume,
            cloud,
            traversal,
            EmptyCounts,
            restrictions);

    private static ScopePreflightEvidence IncompleteEvidence(
        ResolvedScope scope,
        ScopeVolumeKind volume,
        ScopeCloudState cloud) =>
        new(
            scope,
            volume,
            cloud,
            ScopeTraversalState.NotRun,
            EmptyCounts,
            EmptyRestrictions);

    private FileCountResult CountFile(
        string path,
        bool isReparsePoint,
        ScopeOutcomeCounts counts,
        Dictionary<SupportedImageFormat, bool> codecAvailability)
    {
        var format = Recognize(path);
        if (format is null)
        {
            return new FileCountResult(
                new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension + 1,
                    counts.Unreadable,
                    counts.CodecUnavailable),
                ScopeCloudState.Local);
        }

        if (isReparsePoint)
        {
            return new FileCountResult(
                new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension,
                    counts.Unreadable + 1,
                    counts.CodecUnavailable),
                ScopeCloudState.Local);
        }

        if (!codecAvailability.TryGetValue(format.Value, out var codecAvailable))
        {
            codecAvailable = native.IsCodecAvailable(format.Value);
            codecAvailability.Add(format.Value, codecAvailable);
        }

        if (!codecAvailable)
        {
            return new FileCountResult(
                new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension,
                    counts.Unreadable,
                    counts.CodecUnavailable + 1),
                ScopeCloudState.Local);
        }

        var readResult = native.ProbeFileForRead(path);
        if (readResult.CloudState is not ScopeCloudState.Local)
        {
            return new FileCountResult(counts, readResult.CloudState);
        }

        return new FileCountResult(
            readResult.CanOpenForRead
                ? new ScopeOutcomeCounts(
                    counts.Supported + 1,
                    counts.UnsupportedExtension,
                    counts.Unreadable,
                    counts.CodecUnavailable)
                : new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension,
                    counts.Unreadable + 1,
                    counts.CodecUnavailable),
            ScopeCloudState.Local);
    }

    private static SupportedImageFormat? Recognize(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JPG" or ".JPEG" => SupportedImageFormat.Jpeg,
            ".PNG" => SupportedImageFormat.Png,
            ".HEIC" => SupportedImageFormat.Heic,
            _ => null,
        };

    private readonly record struct FileCountResult(
        ScopeOutcomeCounts Counts,
        ScopeCloudState CloudState);

}
