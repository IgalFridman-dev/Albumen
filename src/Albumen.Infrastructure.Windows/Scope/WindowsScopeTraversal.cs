using System.Collections.Immutable;
using System.ComponentModel;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;

namespace Albumen.Infrastructure.Windows.Scope;

internal abstract record WindowsTraversalEvent;

internal sealed record WindowsTraversalRoot(ScopePreflightEvidence Evidence)
    : WindowsTraversalEvent;

internal sealed record WindowsTraversalFile(EnumeratedFile File)
    : WindowsTraversalEvent;

internal sealed record WindowsTraversalFinished(ScopePreflightEvidence Evidence, StageOutcome? ProtectionOutcome = null)
    : WindowsTraversalEvent;

internal sealed class WindowsScopeTraversal
{
    private static readonly ScopeOutcomeCounts EmptyCounts = new(0, 0, 0, 0);
    private static readonly ScopeRestrictionCounts EmptyRestrictions = new(0, 0);

    private readonly IWindowsScopeNative native;

    internal WindowsScopeTraversal(IWindowsScopeNative native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    internal IEnumerable<WindowsTraversalEvent> Walk(
        ScopeSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        var rootAttempt = Capture(() => native.InspectDirectory(selection.SelectedPath));
        if (rootAttempt.Failure is not null)
        {
            var evidence = MapRootInspectionFailure(rootAttempt.Failure);
            yield return new WindowsTraversalFinished(evidence);
            yield break;
        }

        var root = rootAttempt.Value;
        var resolvedScope = new ResolvedScope(selection, root.ResolvedPath, root.Identity);
        var volumeAttempt = Capture(() => native.GetVolumeKind(root.ResolvedPath));
        if (volumeAttempt.Failure is not null)
        {
            var finishedEvidence = MapVolumeFailure(
                volumeAttempt.Failure,
                resolvedScope,
                root.CloudState);
            yield return new WindowsTraversalRoot(
                new ScopePreflightEvidence(
                    resolvedScope,
                    finishedEvidence.Volume,
                    root.CloudState,
                    ScopeTraversalState.NotRun,
                    EmptyCounts,
                    EmptyRestrictions));
            yield return new WindowsTraversalFinished(finishedEvidence);
            yield break;
        }

        var volume = volumeAttempt.Value;
        var rootEvidence = new ScopePreflightEvidence(
            resolvedScope,
            volume,
            root.CloudState,
            ScopeTraversalState.NotRun,
            EmptyCounts,
            EmptyRestrictions);
        if (volume is not ScopeVolumeKind.Fixed and not ScopeVolumeKind.Removable ||
            root.CloudState is not ScopeCloudState.Local)
        {
            yield return new WindowsTraversalRoot(rootEvidence);
            yield return new WindowsTraversalFinished(rootEvidence);
            yield break;
        }

        var rootProtectionAttempt = Capture(() => native.ProtectDirectory(
            resolvedScope, root, selection.SelectedPath, cancellationToken));
        if (rootProtectionAttempt.Failure is { } rootProtectionFailure)
        {
            yield return ProtectionFailure(rootProtectionFailure, true, resolvedScope, volume, EmptyRestrictions);
            yield break;
        }
        using var rootProtection = rootProtectionAttempt.Value;
        yield return new WindowsTraversalRoot(rootEvidence);

        var counts = EmptyCounts;
        var samples = ImmutableArray.CreateBuilder<string>(ScopeOutcomeCounts.MaxSampleSupportedFiles);
        var restrictions = EmptyRestrictions;
        var filePolicy = new WindowsScopeFilePolicy(native);
        var pending = new Stack<DirectoryFrame>();
        var visited = new HashSet<DirectoryIdentity> { root.Identity };
        pending.Push(new DirectoryFrame(root, selection.SelectedPath));

        while (pending.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootValidation = Capture(() => { rootProtection.Validate(cancellationToken); return true; });
            if (rootValidation.Failure is { } rootFailure)
            {
                yield return ProtectionFailure(rootFailure, true, resolvedScope, volume, restrictions);
                yield break;
            }

            // Queued directories own facts only. Acquire their chain at entry,
            // compare the cached identity, and retain it through this listing.
            IWindowsDirectoryProtection? childProtection = null;
            if (frame.Facts.Identity != root.Identity)
            {
                var protectionAttempt = Capture(() => native.ProtectDirectory(
                    resolvedScope, frame.Facts, frame.DiscoveredPath, cancellationToken));
                if (protectionAttempt.Failure is { } protectionFailure)
                {
                    yield return ProtectionFailure(protectionFailure, false, resolvedScope, volume, restrictions);
                    yield break;
                }
                childProtection = protectionAttempt.Value;
            }
            using var currentProtection = childProtection;
            var protection = childProtection ?? rootProtection;
            var entriesAttempt = Capture(() =>
                protection.Enumerate(cancellationToken).GetEnumerator());
            if (entriesAttempt.Failure is not null)
            {
                yield return new WindowsTraversalFinished(
                    MapEnumerationFailure(
                        entriesAttempt.Failure,
                        frame.Facts.Identity == root.Identity,
                        resolvedScope,
                        volume,
                        restrictions));
                yield break;
            }

            using var entries = entriesAttempt.Value;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var moveAttempt = Capture(entries.MoveNext);
                if (moveAttempt.Failure is not null)
                {
                    yield return ProtectionFailure(moveAttempt.Failure,
                        frame.Facts.Identity == root.Identity, resolvedScope, volume, restrictions);
                    yield break;
                }

                if (!moveAttempt.Value)
                {
                    break;
                }

                var entry = entries.Current;
                var discoveredPath = Path.Combine(
                    frame.DiscoveredPath,
                    Path.GetFileName(entry.Path));
                if (entry.IsDirectory)
                {
                    if (selection.Recursion is ScopeRecursion.TopLevelOnly)
                    {
                        continue;
                    }

                    var childAttempt = Capture(() => native.InspectDirectory(entry.Path));
                    if (childAttempt.Failure is not null)
                    {
                        yield return new WindowsTraversalFinished(
                            MapDirectoryFailure(
                                childAttempt.Failure,
                                resolvedScope,
                                volume,
                                restrictions));
                        yield break;
                    }

                    var child = childAttempt.Value;
                    if (!ScopePathContainment.Contains(root.ResolvedPath, child.ResolvedPath))
                    {
                        restrictions = new ScopeRestrictionCounts(
                            restrictions.EscapingReparseTargets + 1,
                            restrictions.DuplicateDirectoryIdentities);
                        continue;
                    }

                    if (entry.CloudState is not ScopeCloudState.Local)
                    {
                        yield return new WindowsTraversalFinished(
                            IncompleteEvidence(resolvedScope, volume, entry.CloudState));
                        yield break;
                    }

                    if (child.CloudState is not ScopeCloudState.Local)
                    {
                        yield return new WindowsTraversalFinished(
                            IncompleteEvidence(resolvedScope, volume, child.CloudState));
                        yield break;
                    }

                    if (!visited.Add(child.Identity))
                    {
                        restrictions = new ScopeRestrictionCounts(
                            restrictions.EscapingReparseTargets,
                            restrictions.DuplicateDirectoryIdentities + 1);
                        continue;
                    }

                    pending.Push(new DirectoryFrame(child, discoveredPath));
                    continue;
                }

                var policyAttempt = Capture(() => filePolicy.Inspect(discoveredPath, entry));
                if (policyAttempt.Failure is not null)
                {
                    yield return new WindowsTraversalFinished(
                        MapEnumerationFailure(
                            policyAttempt.Failure,
                            frame.Facts.Identity == root.Identity,
                            resolvedScope,
                            volume,
                            restrictions));
                    yield break;
                }

                var result = policyAttempt.Value;
                counts = AddFileOutcome(counts, result.File.Outcome);
                if (result.File.Outcome.State is ScanStageState.Succeeded
                    && samples.Count < ScopeOutcomeCounts.MaxSampleSupportedFiles)
                {
                    samples.Add(Path.GetFileName(discoveredPath));
                }
                yield return new WindowsTraversalFile(result.File);
                if (result.BlocksForUnknownFailure)
                {
                    yield return new WindowsTraversalFinished(
                        FailureEvidence(
                            resolvedScope,
                            volume,
                            ScopeCloudState.Local,
                            ScopeTraversalState.Unknown,
                            restrictions));
                    yield break;
                }

                if (result.BlockingCloud is { } cloud)
                {
                    yield return new WindowsTraversalFinished(
                        IncompleteEvidence(resolvedScope, volume, cloud));
                    yield break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalValidation = Capture(() => { rootProtection.Validate(cancellationToken); return true; });
        if (finalValidation.Failure is { } finalFailure)
        {
            yield return ProtectionFailure(finalFailure, true, resolvedScope, volume, restrictions);
            yield break;
        }
        yield return new WindowsTraversalFinished(
            new ScopePreflightEvidence(
                resolvedScope,
                volume,
                ScopeCloudState.Local,
                ScopeTraversalState.Complete,
                new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension,
                    counts.Unreadable,
                    counts.CodecUnavailable,
                    samples.ToImmutable()),
                restrictions));
    }

    private static NativeAttempt<T> Capture<T>(Func<T> action)
    {
        try
        {
            return new NativeAttempt<T>(action(), Failure: null);
        }
        catch (Exception exception)
        {
            return new NativeAttempt<T>(default!, exception);
        }
    }

    private static WindowsTraversalFinished ProtectionFailure(Exception exception, bool isRoot,
        ResolvedScope scope, ScopeVolumeKind volume, ScopeRestrictionCounts restrictions) =>
        new(MapEnumerationFailure(exception, isRoot, scope, volume, restrictions),
            (exception as ScanReadException)?.Outcome);

    private static ScopeOutcomeCounts AddFileOutcome(
        ScopeOutcomeCounts counts,
        StageOutcome outcome) =>
        outcome switch
        {
            { State: ScanStageState.Succeeded } => new ScopeOutcomeCounts(
                counts.Supported + 1,
                counts.UnsupportedExtension,
                counts.Unreadable,
                counts.CodecUnavailable),
            { State: ScanStageState.Unsupported, Reason: ScanReason.UnsupportedExtension } =>
                new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension + 1,
                    counts.Unreadable,
                    counts.CodecUnavailable),
            { State: ScanStageState.Unsupported, Reason: ScanReason.CodecUnavailable } =>
                new ScopeOutcomeCounts(
                    counts.Supported,
                    counts.UnsupportedExtension,
                    counts.Unreadable,
                    counts.CodecUnavailable + 1),
            _ => new ScopeOutcomeCounts(
                counts.Supported,
                counts.UnsupportedExtension,
                counts.Unreadable + 1,
                counts.CodecUnavailable),
        };

    private static ScopePreflightEvidence MapRootInspectionFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => RootFailureEvidence(ScopeTraversalState.RootAccessDenied),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.AccessDenied } =>
                RootFailureEvidence(ScopeTraversalState.RootAccessDenied),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.Unavailable } =>
                new ScopePreflightEvidence(
                    Scope: null,
                    ScopeVolumeKind.Unavailable,
                    ScopeCloudState.Local,
                    ScopeTraversalState.NotRun,
                    EmptyCounts,
                    EmptyRestrictions),
            IOException or Win32Exception => RootFailureEvidence(ScopeTraversalState.Unknown),
            _ => throw exception,
        };

    private static ScopePreflightEvidence MapVolumeFailure(
        Exception exception,
        ResolvedScope scope,
        ScopeCloudState cloud) =>
        exception switch
        {
            UnauthorizedAccessException => FailureEvidence(
                scope,
                ScopeVolumeKind.Unknown,
                cloud,
                ScopeTraversalState.RootAccessDenied,
                EmptyRestrictions),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.AccessDenied } =>
                FailureEvidence(
                    scope,
                    ScopeVolumeKind.Unknown,
                    cloud,
                    ScopeTraversalState.RootAccessDenied,
                    EmptyRestrictions),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.Unavailable } =>
                IncompleteEvidence(scope, ScopeVolumeKind.Unavailable, cloud),
            IOException or Win32Exception => FailureEvidence(
                scope,
                ScopeVolumeKind.Unknown,
                cloud,
                ScopeTraversalState.Unknown,
                EmptyRestrictions),
            _ => throw exception,
        };

    private static ScopePreflightEvidence MapDirectoryFailure(
        Exception exception,
        ResolvedScope scope,
        ScopeVolumeKind volume,
        ScopeRestrictionCounts restrictions) =>
        exception switch
        {
            UnauthorizedAccessException => FailureEvidence(
                scope,
                volume,
                ScopeCloudState.Local,
                ScopeTraversalState.DescendantAccessDenied,
                restrictions),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.AccessDenied } =>
                FailureEvidence(
                    scope,
                    volume,
                    ScopeCloudState.Local,
                    ScopeTraversalState.DescendantAccessDenied,
                    restrictions),
            IOException or Win32Exception => FailureEvidence(
                scope,
                volume,
                ScopeCloudState.Local,
                ScopeTraversalState.Unknown,
                restrictions),
            _ => throw exception,
        };

    private static ScopePreflightEvidence MapEnumerationFailure(
        Exception exception,
        bool isRoot,
        ResolvedScope scope,
        ScopeVolumeKind volume,
        ScopeRestrictionCounts restrictions)
    {
        var accessTraversal = isRoot
            ? ScopeTraversalState.RootAccessDenied
            : ScopeTraversalState.DescendantAccessDenied;
        return exception switch
        {
            ScanReadException { Outcome.Reason: ScanReason.AccessDenied or ScanReason.SharingViolation } =>
                FailureEvidence(scope, volume, ScopeCloudState.Local, accessTraversal, restrictions),
            UnauthorizedAccessException => FailureEvidence(
                scope,
                volume,
                ScopeCloudState.Local,
                accessTraversal,
                restrictions),
            WindowsScopeNativeException { Kind: WindowsScopeFailureKind.AccessDenied } =>
                FailureEvidence(
                    scope,
                    volume,
                    ScopeCloudState.Local,
                    accessTraversal,
                    restrictions),
            IOException or Win32Exception => FailureEvidence(
                scope,
                volume,
                ScopeCloudState.Local,
                ScopeTraversalState.Unknown,
                restrictions),
            _ => throw exception,
        };
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

    private readonly record struct NativeAttempt<T>(T Value, Exception? Failure);

    private sealed record DirectoryFrame(
        WindowsDirectoryFacts Facts,
        string DiscoveredPath);
}
