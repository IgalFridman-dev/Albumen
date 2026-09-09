using System.Runtime.ExceptionServices;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Review;

public sealed class ProtectedReviewWorker
{
    private readonly IProtectedFileReader reader;
    private readonly IExactContentAnalyzer analyzer;
    public ProtectedReviewWorker(IProtectedFileReader reader, IExactContentAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(analyzer);
        this.reader = reader;
        this.analyzer = analyzer;
    }

    public async Task RevalidateAsync(ReviewRevalidationRequest request, IReviewResultSink sink, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        IProtectedFileLease? lease = null;
        ExceptionDispatchInfo? primaryFault = null;
        try
        {
            ReviewRevalidationEvidence? evidence = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (request.Session.Scan != request.Context.Pin || request.Occurrence.Token.Pin != request.Context.Pin)
                {
                    throw new ScanReadException(new(ScanStageState.NotApplicable, ScanReason.Stale), "Review request is stale.");
                }
                var occurrence = request.Occurrence;
                if (occurrence.Exact.State != ScanStageState.Succeeded || occurrence.Enumerate.State != ScanStageState.Succeeded
                    || occurrence.Content is not { } expected || occurrence.PhysicalFileKey is not { } key)
                {
                    throw new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.UnknownSafety), "Review requires proven Scan content.");
                }
                lease = await reader.OpenAsync(new ProtectedOpenRequest(request.Context,
                    occurrence.DiscoveredPath, occurrence.ResolvedPath, key), token).ConfigureAwait(false);
                var opened = lease.OpenedFacts;
                ValidateFacts(request, opened, expected);
                var digest = await analyzer.HashAsync(lease.Content, expected.ByteLength, token).ConfigureAwait(false);
                var final = await lease.RefreshAsync(token).ConfigureAwait(false);
                ValidateFacts(request, final, expected);
                if (new ContentRevision(final.PhysicalFileKey, digest.ByteLength, digest.Sha256) != expected) throw Changed();
                token.ThrowIfCancellationRequested();
                evidence = new ReviewRevalidationEvidence(request, opened, final, digest);
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(request, exception, sink, token).ConfigureAwait(false);
            }

            // Neither admission nor acknowledgment receives worker cancellation.
            // Rejection, stale state, and owner faults settle while the lease lives.
            if (evidence is not null) await sink.CommitRevalidationAsync(evidence).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFault = ExceptionDispatchInfo.Capture(exception);
        }

        // Cleanup runs once, after acknowledgment, without replacing the captured
        // work/owner fault. A successful cleanup preserves its original dispatch.
        try
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalFault) when (primaryFault is not null)
        {
            throw new AggregateException(primaryFault.SourceException, disposalFault);
        }
        primaryFault?.Throw();
    }

    private static void ValidateFacts(ReviewRevalidationRequest request, ProtectedFileFacts facts, ContentRevision expected)
    {
        if (!ScopePathContainment.Contains(request.Context.Scope.ResolvedPath, facts.ResolvedPath))
            throw new ScanReadException(new(ScanStageState.FailedTerminal, ScanReason.UnsafeLocation), "Protected path escaped the approved Scope.");
        if (facts.PhysicalFileKey != expected.PhysicalFileKey || facts.ByteLength != expected.ByteLength
            || !ScopePathContainment.Contains(request.Occurrence.ResolvedPath, facts.ResolvedPath)
            || !ScopePathContainment.Contains(facts.ResolvedPath, request.Occurrence.ResolvedPath)) throw Changed();
    }

    private static ScanReadException Changed() => new(new(ScanStageState.FailedRetryable, ScanReason.Changed), "Protected Review content changed.");

    private static async Task ReportFailureAsync(ReviewRevalidationRequest request, Exception exception,
        IReviewResultSink sink, CancellationToken token)
    {
        var reason = exception switch
        {
            OperationCanceledException when token.IsCancellationRequested => ReviewUnavailableReason.Canceled,
            ScanReadException read => read.Outcome.Reason switch
            {
                ScanReason.Missing => ReviewUnavailableReason.Missing,
                ScanReason.Changed => ReviewUnavailableReason.Changed,
                ScanReason.AccessDenied => ReviewUnavailableReason.AccessDenied,
                ScanReason.SharingViolation => ReviewUnavailableReason.SharingViolation,
                ScanReason.UnsafeLocation => ReviewUnavailableReason.UnsafeLocation,
                ScanReason.UnknownSafety => ReviewUnavailableReason.UnknownSafety,
                ScanReason.ReadFailure => ReviewUnavailableReason.ReadFailure,
                ScanReason.Canceled => ReviewUnavailableReason.Canceled,
                ScanReason.Stale => ReviewUnavailableReason.Stale,
                _ => ReviewUnavailableReason.ProgrammingFault,
            },
            FileNotFoundException or DirectoryNotFoundException => ReviewUnavailableReason.Missing,
            UnauthorizedAccessException => ReviewUnavailableReason.AccessDenied,
            IOException => ReviewUnavailableReason.ReadFailure,
            _ => ReviewUnavailableReason.ProgrammingFault,
        };
        try
        {
            await sink.FailRevalidationAsync(request, reason).ConfigureAwait(false);
        }
        catch (Exception acknowledgmentFault) when (reason == ReviewUnavailableReason.ProgrammingFault)
        {
            throw new AggregateException(exception, acknowledgmentFault);
        }
        if (reason == ReviewUnavailableReason.ProgrammingFault) ExceptionDispatchInfo.Throw(exception);
    }
}
