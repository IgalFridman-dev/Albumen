using System.Collections.Immutable;
using System.Threading.Channels;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;

namespace Albumen.Application.Semantic;

/// <summary>
/// Embeds the Scan's photos on <c>workers</c> parallel workers, one photo each (E4-S1: one
/// inference thread per photo gives 2.5x the throughput of one photo on all threads). At most one
/// decoded photo per worker and <c>workers + </c><see cref="BatchSize"/> vectors are held at once.
/// </summary>
public sealed class BoundedSemanticIndexer : ISemanticIndexer
{
    /// <summary>Most vectors written in one transaction.</summary>
    public const int BatchSize = 64;

    private readonly IVisionLanguageEncoder encoder;
    private readonly IEncoderImageSource images;
    private readonly IEmbeddingStore store;
    private readonly int workers;

    public BoundedSemanticIndexer(
        IVisionLanguageEncoder encoder, IEncoderImageSource images, IEmbeddingStore store, int workers)
    {
        this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        this.images = images ?? throw new ArgumentNullException(nameof(images));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);
        this.workers = workers;
    }

    public async Task<SemanticSnapshot> IndexAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
        Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);
        var eligible = rows
            .Where(row => row.Exact.State == ScanStageState.Succeeded && row.Content is not null)
            .OrderBy(row => row.Token.OccurrenceId)
            .ToImmutableArray();
        var loaded = encoder.LoadProfile();
        var state = new SemanticSnapshot(SemanticState.Indexing, eligible.Length, 0, 0,
            loaded.Profile?.Fingerprint, string.Empty);
        if (loaded is not { Profile: { } profile }) return Stopped(state, loaded.Failure!);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = Channel.CreateBounded<ScanFileOccurrence>(new BoundedChannelOptions(workers)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var done = Channel.CreateBounded<Encoded>(new BoundedChannelOptions(workers)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Task? pipeline = null;
        var batch = new List<StoredEmbedding>(BatchSize);
        try
        {
            // One index holds one Scan under one profile: vectors of earlier Scans or profiles go first.
            await store.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (eligible.IsEmpty) return Finished(state);
            await progress(state).ConfigureAwait(false);
            pipeline = RunAsync(context, profile, eligible, work, done.Writer, stop.Token);
            await foreach (var item in done.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Failure is { } failure) return Stopped(state, failure);
                state = state with { Processed = state.Processed + 1 };
                if (item.Vector is { } vector)
                    batch.Add(new(item.Row.Token.OccurrenceId, item.Row.Content!.Sha256, vector));
                if (batch.Count < BatchSize && done.Reader.TryPeek(out _)) continue;
                if (batch.Count != 0)
                {
                    await store.AddAsync(context.Pin.ScanId, profile, batch, cancellationToken).ConfigureAwait(false);
                    state = state with { Indexed = state.Indexed + batch.Count };
                    batch.Clear();
                }
                await progress(state).ConfigureAwait(false);
            }
            await pipeline.ConfigureAwait(false);
            return Finished(state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finished(state);
        }
        catch (Exception)
        {
            return state with { State = SemanticState.Failed, Reason = "The photo search data could not be saved." };
        }
        finally
        {
            // No worker outlives the index: leases close and no vector is written after return.
            await stop.CancelAsync().ConfigureAwait(false);
            if (pipeline is not null)
            {
                try { await pipeline.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static SemanticSnapshot Finished(SemanticSnapshot state) =>
        state with { State = state.Indexed > 0 ? SemanticState.Ready : SemanticState.Empty };

    private static SemanticSnapshot Stopped(SemanticSnapshot state, EncoderFailure failure) => state with
    {
        State = failure.Kind == EncoderFailureKind.Unavailable ? SemanticState.Unavailable : SemanticState.Failed,
        Reason = failure.Reason,
    };

    private async Task RunAsync(ScanRunContext context, EncoderProfile profile,
        ImmutableArray<ScanFileOccurrence> eligible, Channel<ScanFileOccurrence> work,
        ChannelWriter<Encoded> done, CancellationToken token)
    {
        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(() => WorkAsync(context, profile, work.Reader, done, token), CancellationToken.None))
            .Append(ProduceAsync(eligible, work.Writer, token))
            .ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            done.TryComplete();
        }
    }

    private static async Task ProduceAsync(ImmutableArray<ScanFileOccurrence> eligible,
        ChannelWriter<ScanFileOccurrence> writer, CancellationToken token)
    {
        try
        {
            foreach (var row in eligible)
                await writer.WriteAsync(row, token).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task WorkAsync(ScanRunContext context, EncoderProfile profile,
        ChannelReader<ScanFileOccurrence> reader, ChannelWriter<Encoded> done, CancellationToken token)
    {
        await foreach (var row in reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            Encoded result;
            try
            {
                var image = await images.ReadAsync(context, row, profile.DecodeEdge, token).ConfigureAwait(false);
                result = image is null ? new(row, null, null) : Encode(profile, row, image);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = new(row, null, null);
            }
            catch (Exception)
            {
                result = new(row, null, new(EncoderFailureKind.Failed, "Preparing a photo for search failed unexpectedly."));
            }
            await done.WriteAsync(result, token).ConfigureAwait(false);
        }
    }

    private Encoded Encode(EncoderProfile profile, ScanFileOccurrence row, EncoderImage image)
    {
        var embedding = encoder.EncodeImage(profile, image);
        return embedding.Failure is { } failure
            ? new(row, null, failure)
            : new(row, embedding.Vector.ToArray(), null);
    }

    private sealed record Encoded(ScanFileOccurrence Row, float[]? Vector, EncoderFailure? Failure);
}
