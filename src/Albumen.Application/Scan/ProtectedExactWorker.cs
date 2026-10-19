using System.Runtime.ExceptionServices;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

public sealed class ProtectedExactWorker(IProtectedFileReader reader, IExactContentAnalyzer analyzer)
{
    public async Task AnalyzeAsync(OccurrenceWork work, IScanResultSink sink, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(sink);
        var commit = await SignAsync(work, sink, token).ConfigureAwait(false);
        if (commit is not { Accepted: true }) return;

        // SignAsync has disposed its lease before either comparison lease opens.
        foreach (var comparison in commit.Comparisons)
        {
            if (token.IsCancellationRequested) break;
            await CompareAsync(comparison, work.Token, sink, token).ConfigureAwait(false);
        }
    }

    private async Task<SignatureCommit?> SignAsync(OccurrenceWork work, IScanResultSink sink, CancellationToken token)
    {
        IProtectedFileLease? lease = null;
        try
        {
            SignatureEvidence evidence;
            try
            {
                lease = await reader.OpenAsync(Request(work), token).ConfigureAwait(false);
                var opened = lease.OpenedFacts;
                var digest = await analyzer.HashAsync(lease.Content, opened.ByteLength, token).ConfigureAwait(false);
                var final = await lease.RefreshAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ValidateFacts(work, opened, final, digest);
                evidence = new SignatureEvidence(work.Token,
                    new ContentRevision(final.PhysicalFileKey, digest.ByteLength, digest.Sha256), opened, final);
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(work.Token, exception, sink, token).ConfigureAwait(false);
                return null;
            }

            // An admitted result owns its acknowledgment independently of worker
            // cancellation. Even rejection or an owner fault must settle first.
            return await sink.CommitSignatureAsync(evidence).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CompareAsync(ComparisonWork work, OccurrenceToken initiatingToken,
        IScanResultSink sink, CancellationToken token)
    {
        IProtectedFileLease? left = null;
        IProtectedFileLease? right = null;
        OccurrenceReadStream? leftStream = null;
        OccurrenceReadStream? rightStream = null;
        var failingToken = work.Left.Token;
        try
        {
            ComparisonEvidence evidence;
            try
            {
                left = await reader.OpenAsync(Request(work.Left), token).ConfigureAwait(false);
                ValidateOpened(work.Left, left.OpenedFacts, work.LeftContent);
                failingToken = work.Right.Token;
                right = await reader.OpenAsync(Request(work.Right), token).ConfigureAwait(false);
                ValidateOpened(work.Right, right.OpenedFacts, work.RightContent);

                // The stream adapter attributes read/length failures without
                // exposing occurrence identity to the Analysis port.
                leftStream = new OccurrenceReadStream(left.Content, work.LeftContent.ByteLength);
                rightStream = new OccurrenceReadStream(right.Content, work.RightContent.ByteLength);
                failingToken = initiatingToken;
                var content = await analyzer.CompareAsync(leftStream, rightStream,
                    work.LeftContent.ByteLength, token).ConfigureAwait(false);
                failingToken = work.Left.Token;
                var leftFinal = await left.RefreshAsync(token).ConfigureAwait(false);
                ValidateFacts(work.Left, left.OpenedFacts, leftFinal, content.Left);
                ValidateRevision(leftFinal, content.Left, work.LeftContent);
                failingToken = work.Right.Token;
                var rightFinal = await right.RefreshAsync(token).ConfigureAwait(false);
                ValidateFacts(work.Right, right.OpenedFacts, rightFinal, content.Right);
                ValidateRevision(rightFinal, content.Right, work.RightContent);
                token.ThrowIfCancellationRequested();
                evidence = new ComparisonEvidence(work, content.Equal, leftFinal, rightFinal, content);
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(leftStream?.Failure, exception)) failingToken = work.Left.Token;
                else if (ReferenceEquals(rightStream?.Failure, exception)) failingToken = work.Right.Token;
                await ReportFailureAsync(failingToken, exception, sink, token).ConfigureAwait(false);
                return;
            }

            await sink.CommitComparisonAsync(evidence).ConfigureAwait(false);
        }
        finally
        {
            // Failure acknowledgment above also runs with every acquired lease
            // alive. Dispose the peer even if the first disposal itself fails.
            try
            {
                if (right is not null) await right.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (left is not null) await left.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static ProtectedOpenRequest Request(OccurrenceWork work) => new(work.Context,
        work.File.DiscoveredPath, work.File.ResolvedPath,
        work.File.PhysicalFileKey ?? throw new InvalidOperationException("Accepted work requires physical identity."));

    private static void ValidateOpened(OccurrenceWork work, ProtectedFileFacts opened, ContentRevision expected)
    {
        if (opened.PhysicalFileKey != work.File.PhysicalFileKey || opened.PhysicalFileKey != expected.PhysicalFileKey
            || opened.ByteLength != expected.ByteLength) throw Changed();
    }

    private static void ValidateFacts(OccurrenceWork work, ProtectedFileFacts opened,
        ProtectedFileFacts final, StreamDigest digest)
    {
        if (opened.PhysicalFileKey != work.File.PhysicalFileKey || opened.PhysicalFileKey != final.PhysicalFileKey
            || opened.ByteLength != final.ByteLength || digest.ByteLength != final.ByteLength) throw Changed();
    }

    private static void ValidateRevision(ProtectedFileFacts final, StreamDigest digest, ContentRevision expected)
    {
        if (new ContentRevision(final.PhysicalFileKey, digest.ByteLength, digest.Sha256) != expected) throw Changed();
    }

    private static ScanReadException Changed() => new(
        new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed), "Protected content changed.");

    private static async Task ReportFailureAsync(OccurrenceToken occurrence, Exception exception,
        IScanResultSink sink, CancellationToken token)
    {
        var outcome = exception switch
        {
            OperationCanceledException when token.IsCancellationRequested =>
                new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled),
            ScanReadException read => read.Outcome,
            FileNotFoundException or DirectoryNotFoundException =>
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Missing),
            UnauthorizedAccessException => new StageOutcome(ScanStageState.FailedRetryable, ScanReason.AccessDenied),
            IOException => new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            _ => new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault),
        };
        try
        {
            await sink.FailAsync(occurrence, outcome).ConfigureAwait(false);
        }
        catch (Exception acknowledgmentFault) when (outcome.Reason == ScanReason.ProgrammingFault)
        {
            throw new AggregateException(exception, acknowledgmentFault);
        }
        if (outcome.Reason == ScanReason.ProgrammingFault) ExceptionDispatchInfo.Throw(exception);
    }

    // Leave-open, forward-only view. Length validation belongs at this boundary
    // too: the analyzer otherwise reports EOF/extra bytes without a side.
    private sealed class OccurrenceReadStream(Stream content, long expectedLength) : Stream
    {
        private long bytesRead;
        public Exception? Failure { get; private set; }
        public override bool CanRead => content.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expectedLength;
        public override long Position { get => bytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            try { return Observe(content.Read(buffer), buffer.Length); }
            catch (Exception exception) { Failure = exception; throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return Observe(await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length); }
            catch (Exception exception) { Failure = exception; throw; }
        }
        private int Observe(int count, int requested)
        {
            bytesRead = checked(bytesRead + count);
            if (requested != 0 && ((count == 0 && bytesRead < expectedLength) || bytesRead > expectedLength))
                throw new IOException("Stream length differs from the protected content length.");
            return count;
        }
    }
}
