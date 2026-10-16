using System.Collections.Immutable;
using System.Threading.Channels;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

public interface INearFeatureSource
{
    ValueTask<NearFeatureResult> AnalyzeAsync(
        ScanRunContext context, ScanFileOccurrence row, CancellationToken cancellationToken);
}

public interface IPerceptualSignatureAnalyzer
{
    PerceptualSignature Extract(int width, int height, ReadOnlySpan<byte> bgra);
}

public interface INearDuplicatePipeline
{
    Task<NearDuplicateBatch> AnalyzeAsync(ScanRunContext context,
        ImmutableArray<ScanFileOccurrence> rows, CancellationToken cancellationToken);
}

public sealed class BoundedNearDuplicatePipeline(INearFeatureSource source) : INearDuplicatePipeline
{
    public const int Capacity = 16;
    public const string ImplementationRevision = "managed-dhash-bgra-v1";

    public async Task<NearDuplicateBatch> AnalyzeAsync(ScanRunContext context,
        ImmutableArray<ScanFileOccurrence> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var channel = Channel.CreateBounded<ScanFileOccurrence>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        var results = ImmutableArray.CreateBuilder<NearFeatureResult>(rows.Length);
        var producer = ProduceAsync(channel.Writer, rows, cancellationToken);
        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var result = await source.AnalyzeAsync(context, row, cancellationToken).ConfigureAwait(false);
                results.Add(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var outcome = exception is IOException or UnauthorizedAccessException
                    ? new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure)
                    : new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault);
                results.Add(new NearFeatureResult(row.Token, row.Content, null, outcome));
            }
        }
        await producer.ConfigureAwait(false);
        return new NearDuplicateBatch(context.Pin, ImplementationRevision, results.ToImmutable());
    }

    private static async Task ProduceAsync(ChannelWriter<ScanFileOccurrence> writer,
        ImmutableArray<ScanFileOccurrence> rows, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            foreach (var row in rows.OrderBy(row => row.Token.OccurrenceId))
                await writer.WriteAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally { writer.TryComplete(failure); }
    }
}
