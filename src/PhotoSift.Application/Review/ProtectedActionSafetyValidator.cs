using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

public sealed class ProtectedActionSafetyValidator
{
    private readonly IProtectedFileReader reader;
    private readonly IExactContentAnalyzer analyzer;

    public ProtectedActionSafetyValidator(IProtectedFileReader reader, IExactContentAnalyzer analyzer)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    // The callback owns admission and acknowledgment. No worker cancellation is
    // passed to it, and all acquired leases survive until that receipt settles.
    public async Task<T> ProbeAsync<T>(ScanRunContext context,
        ImmutableArray<CandidateGroup> groups, ImmutableHashSet<long> recycledOccurrenceIds,
        Func<ImmutableArray<ActionGroupProbeResult>, ActionSafetyReason, Task<T>> acknowledge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recycledOccurrenceIds);
        ArgumentNullException.ThrowIfNull(acknowledge);
        if (groups.IsDefault) throw new ArgumentException("Probe groups must be initialized.", nameof(groups));
        var leases = new List<IProtectedFileLease>();
        var results = ImmutableArray.CreateBuilder<ActionGroupProbeResult>();
        var failure = ActionSafetyReason.None;
        ExceptionDispatchInfo? fault = null;
        T result = default!;
        try
        {
            foreach (var group in groups)
            {
                var observations = ImmutableArray.CreateBuilder<ActionProbeObservation>();
                var missing = ImmutableArray.CreateBuilder<long>();
                foreach (var occurrence in group.Members)
                {
                    if (recycledOccurrenceIds.Contains(occurrence.Token.OccurrenceId)) continue;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (group.Pin != context.Pin || occurrence.Token.Pin != context.Pin
                            || occurrence.Content is null || occurrence.PhysicalFileKey is null
                            || occurrence.Exact.State != ScanStageState.Succeeded
                            || occurrence.Enumerate.State != ScanStageState.Succeeded)
                            throw new ScanReadException(new(ScanStageState.NotApplicable, ScanReason.Stale), "Stale Action probe.");
                        var lease = await reader.OpenAsync(new(context, occurrence.DiscoveredPath,
                            occurrence.ResolvedPath, occurrence.PhysicalFileKey.Value), cancellationToken).ConfigureAwait(false);
                        leases.Add(lease);
                        var opened = lease.OpenedFacts;
                        ValidateFacts(context, occurrence, opened);
                        var digest = await analyzer.HashAsync(lease.Content,
                            occurrence.Content.ByteLength, cancellationToken).ConfigureAwait(false);
                        var final = await lease.RefreshAsync(cancellationToken).ConfigureAwait(false);
                        ValidateFacts(context, occurrence, final);
                        if (new ContentRevision(final.PhysicalFileKey, digest.ByteLength, digest.Sha256) != occurrence.Content)
                            throw Changed();
                        cancellationToken.ThrowIfCancellationRequested();
                        observations.Add(new(occurrence, opened, final, digest));
                    }
                    catch (Exception exception)
                    {
                        missing.Add(occurrence.Token.OccurrenceId);
                        var reason = FailureReason(exception, cancellationToken);
                        if (failure == ActionSafetyReason.None || reason == ActionSafetyReason.ProgrammingFault) failure = reason;
                    }
                }
                results.Add(new(group.GroupId, group.GroupRevision, observations.ToImmutable(), missing.ToImmutable()));
            }
            result = await acknowledge(results.ToImmutable(), failure).ConfigureAwait(false);
        }
        catch (Exception exception) { fault = ExceptionDispatchInfo.Capture(exception); }

        List<Exception>? disposalFaults = null;
        foreach (var lease in leases)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (disposalFaults ??= []).Add(exception); }
        }
        if (disposalFaults is not null)
        {
            if (fault is not null) disposalFaults.Insert(0, fault.SourceException);
            throw new AggregateException(disposalFaults);
        }
        fault?.Throw();
        return result;
    }

    private static void ValidateFacts(ScanRunContext context, ScanFileOccurrence occurrence, ProtectedFileFacts facts)
    {
        if (!ScopePathContainment.Contains(context.Scope.ResolvedPath, facts.ResolvedPath))
            throw new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation), "Action probe escaped Scope.");
        if (facts.PhysicalFileKey != occurrence.PhysicalFileKey || facts.ByteLength != occurrence.Content!.ByteLength
            || !ScopePathContainment.Contains(occurrence.ResolvedPath, facts.ResolvedPath)
            || !ScopePathContainment.Contains(facts.ResolvedPath, occurrence.ResolvedPath)) throw Changed();
    }

    private static ScanReadException Changed() => new(new(ScanStageState.FailedRetryable, ScanReason.Changed), "Action probe facts changed.");

    private static ActionSafetyReason FailureReason(Exception exception, CancellationToken token) => exception switch
    {
        OperationCanceledException when token.IsCancellationRequested => ActionSafetyReason.Interrupted,
        ScanReadException read => read.Outcome.Reason switch
        {
            ScanReason.Missing => ActionSafetyReason.Missing,
            ScanReason.Changed => ActionSafetyReason.Changed,
            ScanReason.AccessDenied => ActionSafetyReason.AccessDenied,
            ScanReason.SharingViolation => ActionSafetyReason.SharingViolation,
            ScanReason.UnsafeLocation => ActionSafetyReason.UnsafeLocation,
            ScanReason.UnknownSafety => ActionSafetyReason.UnknownSafety,
            ScanReason.Stale => ActionSafetyReason.StalePin,
            ScanReason.Canceled => ActionSafetyReason.Interrupted,
            ScanReason.ReadFailure => ActionSafetyReason.Unavailable,
            _ => ActionSafetyReason.ProgrammingFault,
        },
        FileNotFoundException or DirectoryNotFoundException => ActionSafetyReason.Missing,
        UnauthorizedAccessException => ActionSafetyReason.AccessDenied,
        IOException => ActionSafetyReason.Unavailable,
        _ => ActionSafetyReason.ProgrammingFault,
    };
}
