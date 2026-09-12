using System.Collections.Immutable;
using System.Security.Cryptography;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Windows.Scan;

namespace PhotoSift.Infrastructure.Windows.Action;

// Created by the executor only after Executing. Ownership is confined to its
// one-item execution context; PreDelete and cleanup run serially on that context.
internal sealed class WindowsActionLease : IDisposable
{
    private readonly ActionSafetyEnvelope safety;
    private readonly WindowsFileFactsReader facts;
    private readonly List<PathProof> paths = [];
    private readonly Dictionary<PhysicalFileKey, ContentProof> contents = [];
    private ContentProof? selected;
    private FileStream? handoff;
    private FileStream? reacquired;
    private bool handoffAttempted;
    private bool consumed;
    private bool disposed;
    internal bool NamespaceHandoffOccurred { get; private set; }

    private WindowsActionLease(ActionSafetyEnvelope safety, IWindowsFileFactsSource source)
    {
        this.safety = safety;
        facts = new(new WindowsActionFileNative(source));
    }

    internal static async Task<WindowsActionLease> OpenAsync(ActionSafetyEnvelope safety,
        CancellationToken cancellationToken, IWindowsFileFactsSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(safety);
        var lease = new WindowsActionLease(safety, source ?? new WindowsFileNative());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lease.ValidateEnvelope();
            var context = new ScanRunContext(safety.Claim.Request.Scan, safety.Claim.Request.Scope);
            // Always acquire the selected original name first. A same-key alias
            // never acquires a second content handle that could deny its deletion.
            var ordered = safety.Input.CurrentOccurrences.OrderByDescending(value => value.Token == safety.Item.Occurrence);
            foreach (var occurrence in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new ProtectedOpenRequest(context, occurrence.DiscoveredPath, occurrence.ResolvedPath, occurrence.PhysicalFileKey!.Value);
                var chain = lease.facts.ValidateScope(request, null, cancellationToken, retainDirectoryGuards: true);
                var path = new PathProof(occurrence, request, chain, chain.Proofs.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
                lease.paths.Add(path);
                using var metadata = WindowsFileNative.OpenMetadata(WindowsFileFactsReader.NormalizePath(request.DiscoveredPath));
                var observed = lease.facts.ReadContent(metadata, request);
                var previous = safety.Observations.Single(value => value.ApprovedOccurrence.Token == occurrence.Token).Final;
                RequireSame(observed, previous);
                if (!lease.contents.TryGetValue(occurrence.PhysicalFileKey.Value, out var content))
                {
                    var handle = WindowsFileNative.OpenContent(WindowsFileFactsReader.NormalizePath(request.DiscoveredPath));
                    FileStream stream;
                    try { stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false); }
                    catch { handle.Dispose(); throw; }
                    content = new(stream, request, observed, occurrence.Content!);
                    lease.contents.Add(occurrence.PhysicalFileKey.Value, content);
                    RequireSame(lease.facts.ReadContent(stream.SafeFileHandle, request, observed), observed);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                    Require(hash == occurrence.Content!.Sha256 && stream.Length == occurrence.Content.ByteLength, ActionSafetyReason.Changed);
                    RequireSame(lease.facts.ReadContent(stream.SafeFileHandle, request, observed), observed);
                }
                else
                {
                    Require(content.Revision == occurrence.Content, ActionSafetyReason.Changed);
                    Require(observed.Allocation == content.Opened.Allocation, ActionSafetyReason.InconsistentLinks);
                }
                if (occurrence.Token == safety.Item.Occurrence) lease.selected = content;
            }
            cancellationToken.ThrowIfCancellationRequested();
            lease.ValidateCurrent();
            return lease;
        }
        catch (Exception exception)
        {
            lease.Dispose();
            throw new WindowsActionLeaseException(WindowsActionFileNative.Reason(exception), exception);
        }
    }

    // Explicit Action-only preparation. A direct Task 6 caller keeps the original
    // strict lease unless it requests this one-shot namespace handoff.
    internal ActionSafetyReason PrepareForPerformOperations()
    {
        if (disposed || consumed || handoffAttempted) return ActionSafetyReason.Changed;
        handoffAttempted = true;
        try
        {
            ValidateCurrent();
            var content = selected!;
            var handle = WindowsActionFileNative.OpenSelectedHandoff(content.Request.DiscoveredPath);
            try { handoff = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false); }
            catch { handle.Dispose(); throw; }
            RequireSame(facts.ReadContent(handoff.SafeFileHandle, content.Request, content.Opened), content.Opened);
            var originalStrictHandle = content.Stream.SafeFileHandle;
            try { content.Stream.Dispose(); }
            finally
            {
                // Preserve the transition even if disposal reports a failure after
                // closing the strict handle. It is never reset by denial/cleanup.
                NamespaceHandoffOccurred = originalStrictHandle.IsClosed;
            }
            return ActionSafetyReason.None;
        }
        catch (Exception exception)
        {
            consumed = true;
            return WindowsActionFileNative.Reason(exception);
        }
    }

    // This entire boundary is synchronous: retained hashes are protected by
    // share denial. Never add hashing, awaits, UI/owner or persistence here.
    internal ActionSafetyReason ValidateAndReleaseForPreDelete()
    {
        if (disposed || consumed) return ActionSafetyReason.Changed;
        consumed = true; // A denied/repeated callback can never retry this lease.
        try
        {
            if (NamespaceHandoffOccurred)
            {
                var previous = selected!;
                // Acquire strict protection BEFORE any latest facts read, including
                // selected identity. No weak-handle or alternate-path fallback exists.
                var handle = WindowsFileNative.OpenContent(WindowsFileFactsReader.NormalizePath(previous.Request.DiscoveredPath));
                try { reacquired = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false); }
                catch { handle.Dispose(); throw; }
                RequireSame(facts.ReadContent(reacquired.SafeFileHandle, previous.Request, previous.Opened), previous.Opened);
                RequireSame(facts.ReadContent(handoff!.SafeFileHandle, previous.Request, previous.Opened), previous.Opened);
                selected = previous with { Stream = reacquired };
                contents[previous.Opened.PhysicalFileKey] = selected;
            }
            ValidateCurrent();
            // All temporary metadata handles are now gone; distinct witnesses
            // and directory guards remain until deterministic executor cleanup.
            // Close permissive first; the final strict release immediately precedes
            // S_OK. Namespace handoff was separately observed, never permission.
            handoff?.Dispose();
            selected!.Stream.Dispose();
            return ActionSafetyReason.None;
        }
        catch (Exception exception) when (!NamespaceHandoffOccurred
            || exception is WindowsActionLeaseException or ScanReadException)
        {
            // Preserve direct Task 6 behavior and expected native/safety denials.
            // An unexpected exception after handoff reaches the real progress sink
            // as CallbackException, so an unchanged-object proof cannot hide it.
            return WindowsActionFileNative.Reason(exception);
        }
    }

    private void ValidateCurrent()
    {
        Require(selected is not null, ActionSafetyReason.StalePin);
        var currentFacts = new Dictionary<PhysicalFileKey, ProtectedFileFacts>();
        foreach (var pair in contents)
        {
            var content = pair.Value;
            var current = facts.ReadContent(content.Stream.SafeFileHandle, content.Request, content.Opened);
            RequireSame(current, content.Opened);
            currentFacts.Add(pair.Key, current);
        }
        var currentOccurrences = ImmutableArray.CreateBuilder<ScanFileOccurrence>();
        foreach (var path in paths)
        {
            using var refreshedChain = facts.ValidateScope(path.Request, path.Proofs, CancellationToken.None, retainDirectoryGuards: true);
            using var original = WindowsFileNative.OpenMetadata(WindowsFileFactsReader.NormalizePath(path.Request.DiscoveredPath));
            var named = facts.ReadContent(original, path.Request);
            var held = currentFacts[path.Occurrence.PhysicalFileKey!.Value];
            Require(named.PhysicalFileKey == held.PhysicalFileKey && named.ByteLength == held.ByteLength
                && named.LastWriteUtc == held.LastWriteUtc && named.Allocation == held.Allocation,
                ActionSafetyReason.Changed);
            var occurrence = path.Occurrence;
            currentOccurrences.Add(new(occurrence.Token, occurrence.DiscoveredPath, occurrence.ResolvedPath,
                named.PhysicalFileKey, occurrence.Enumerate, occurrence.Exact, occurrence.Content, held.Allocation));
        }
        // Domain compares current stable NumberOfLinks with the exact distinct
        // current known paths. No inferred recycle decrement or recovery credit.
        var decision = ActionSafetyPolicy.Evaluate(safety.Input with { CurrentOccurrences = currentOccurrences.ToImmutable() });
        Require(decision.Kind == ActionSafetyDecisionKind.Allowed, decision.Reason);
    }

    private void ValidateEnvelope()
    {
        var item = safety.Item;
        Require(safety.Claim.Request.Items.Contains(item) && item.Occurrence.Pin == safety.Claim.Request.Scan
            && safety.Input.ApprovedGroup.GroupId == item.GroupId && safety.Input.ApprovedGroup.GroupRevision == item.GroupRevision
            && safety.Input.RemainingApprovedOccurrenceIds.Contains(item.Occurrence.OccurrenceId), ActionSafetyReason.StalePin);
        var decision = ActionSafetyPolicy.Evaluate(safety.Input);
        Require(safety.Decision.Kind == ActionSafetyDecisionKind.Allowed && decision.Kind == ActionSafetyDecisionKind.Allowed, decision.Reason);
        Require(!safety.Observations.IsDefault && safety.Observations.Length == safety.Input.CurrentOccurrences.Length, ActionSafetyReason.UnknownSafety);
        foreach (var occurrence in safety.Input.CurrentOccurrences)
        {
            var matching = safety.Observations.Where(value => value.ApprovedOccurrence.Token == occurrence.Token).ToArray();
            Require(matching.Length == 1, ActionSafetyReason.UnknownSafety);
            var observation = matching[0];
            Require(observation.ApprovedOccurrence.DiscoveredPath == occurrence.DiscoveredPath
                && observation.ApprovedOccurrence.ResolvedPath == occurrence.ResolvedPath
                && observation.ApprovedOccurrence.Content == occurrence.Content
                && observation.Final.PhysicalFileKey == occurrence.PhysicalFileKey
                && observation.Final.ByteLength == occurrence.Content!.ByteLength
                && observation.Digest.ByteLength == occurrence.Content.ByteLength
                && observation.Digest.Sha256 == occurrence.Content.Sha256, ActionSafetyReason.Changed);
            if (occurrence.Token == item.Occurrence)
                Require(occurrence.DiscoveredPath == item.DiscoveredPath && occurrence.ResolvedPath == item.ResolvedPath
                    && occurrence.PhysicalFileKey == item.PhysicalFileKey && occurrence.Content == item.Content, ActionSafetyReason.Changed);
        }
    }

    private static void RequireSame(ProtectedFileFacts current, ProtectedFileFacts expected)
    {
        Require(current.PhysicalFileKey == expected.PhysicalFileKey && current.ResolvedPath == expected.ResolvedPath
            && current.ByteLength == expected.ByteLength && current.LastWriteUtc == expected.LastWriteUtc, ActionSafetyReason.Changed);
        Require(current.Allocation.Stable && current.Allocation == expected.Allocation, ActionSafetyReason.InconsistentLinks);
    }

    private static void Require(bool condition, ActionSafetyReason reason)
    {
        if (!condition) throw new WindowsActionLeaseException(reason == ActionSafetyReason.None ? ActionSafetyReason.UnknownSafety : reason);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { handoff?.Dispose(); }
        finally
        {
            try { reacquired?.Dispose(); }
            finally
            {
                try { foreach (var content in contents.Values) content.Stream.Dispose(); }
                finally { for (var index = paths.Count - 1; index >= 0; index--) paths[index].Guard.Dispose(); }
            }
        }
    }

    private sealed record PathProof(ScanFileOccurrence Occurrence, ProtectedOpenRequest Request,
        WindowsScopeReadGuard Guard, ImmutableDictionary<string, WindowsDirectoryProof> Proofs);
    private sealed record ContentProof(FileStream Stream, ProtectedOpenRequest Request, ProtectedFileFacts Opened, ContentRevision Revision);
}
