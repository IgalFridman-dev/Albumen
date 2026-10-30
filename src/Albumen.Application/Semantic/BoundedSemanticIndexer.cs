using System.Collections.Immutable;
using System.Threading.Channels;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;

namespace Albumen.Application.Semantic;

/// <summary>
/// Embeds the Scan's photos on <c>workers</c> parallel workers, one photo each (E4-S1: one
/// inference thread per photo gives 2.5x the throughput of one photo on all threads). At most one
/// decoded photo per worker and <c>workers + </c><see cref="BatchSize"/> vectors are held at once.
/// A vector stored for the photo's proven content under the same profile is reused without opening
/// the photo; a new one is stored for the next Scan. A Ready index then has the encoder open its
/// text model in the background, so the first search does not wait for it.
/// </summary>
public sealed class BoundedSemanticIndexer : ISemanticIndexer
{
    /// <summary>Most vectors written in one transaction.</summary>
    public const int BatchSize = 64;

    /// <summary>Longest a vector waits to be saved and counted, in milliseconds.</summary>
    public const int ReportInterval = 250;

    private readonly IVisionLanguageEncoder encoder;
    private readonly IEncoderImageSource images;
    private readonly IEmbeddingStore store;
    private readonly ISignatureStore signatures;
    private readonly int workers;

    public BoundedSemanticIndexer(IVisionLanguageEncoder encoder, IEncoderImageSource images,
        IEmbeddingStore store, ISignatureStore signatures, int workers)
    {
        this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        this.images = images ?? throw new ArgumentNullException(nameof(images));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);
        this.workers = workers;
    }

    public async Task<SemanticSnapshot> IndexAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
        Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(progress);
        var loaded = encoder.LoadProfile();
        var result = await EmbedAsync(context, rows, loaded, progress, cancellationToken).ConfigureAwait(false);
        // Ready means photos were embedded, so under this profile.
        if (result.State == SemanticState.Ready && loaded.Profile is { } profile) OpenTextModelInTheBackground(profile);
        return result;
    }

    // The b9e6761 walkthrough (owner, 2026-10-25): the first search waited seconds while the text
    // model was verified and opened. Opened here, after the last photo, the first search is as quick
    // as the next. Nothing waits for it: a failure is the one the search reports when it runs, and
    // the index and the Scan stay as they are.
    private void OpenTextModelInTheBackground(EncoderProfile profile) =>
        _ = Task.Run(() =>
        {
            try
            {
                _ = encoder.PrepareText(profile);
            }
            catch (Exception)
            {
                // The search opens the model itself and says why it can't.
            }
        }, CancellationToken.None);

    private async Task<SemanticSnapshot> EmbedAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
        EncoderProfileResult loaded, Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken)
    {
        var eligible = rows
            .Where(row => row.Exact.State == ScanStageState.Succeeded && row.Content is not null)
            .OrderBy(row => row.Token.OccurrenceId)
            .ToImmutableArray();
        var state = new SemanticSnapshot(SemanticState.Indexing, eligible.Length, 0, 0,
            loaded.Profile?.Fingerprint, string.Empty);
        if (loaded is not { Profile: { } profile }) return Stopped(state, loaded.Failure!);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = Channel.CreateBounded<Work>(new BoundedChannelOptions(workers)
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
        var fresh = new List<StoredSignature>(BatchSize);
        try
        {
            // One index holds one Scan under one profile: vectors of earlier Scans or profiles go first.
            await store.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (eligible.IsEmpty) return Finished(state);
            await progress(state).ConfigureAwait(false);
            pipeline = RunAsync(context, profile, eligible, work, done.Writer, stop.Token);
            var reported = 0;
            var flushAt = 0L;
            while (true)
            {
                if (!done.Reader.TryRead(out var item))
                {
                    // Vectors are saved and reported in batches: when full, or when ReportInterval has
                    // passed since the first unsaved one, so a 50k-photo Scan is not 50k UI updates.
                    var wait = state.Processed == reported
                        ? Timeout.Infinite
                        : (int)Math.Max(0, flushAt - Environment.TickCount64);
                    var ready = await WaitToReadAsync(done.Reader, wait, cancellationToken).ConfigureAwait(false);
                    if (ready == false) break;
                    if (ready is null)
                    {
                        state = await SaveAsync(context, profile, batch, fresh, state, progress, cancellationToken).ConfigureAwait(false);
                        reported = state.Processed;
                    }
                    continue;
                }
                if (item.Failure is { } failure) return Stopped(state, failure);
                if (state.Processed == reported) flushAt = Environment.TickCount64 + ReportInterval;
                state = state with { Processed = state.Processed + 1, Vectors = Count(state.Vectors, item.Found) };
                if (item.Vector is { } vector)
                {
                    batch.Add(new(item.Row.Token.OccurrenceId, item.Row.Content!.Sha256, vector));
                    // Only a missing vector is stored; a rejected one keeps what is stored.
                    if (item.Found == Found.Miss)
                        fresh.Add(new(Key(item.Row, profile), SignatureCodec.EncodeVector(vector)));
                }
                if (batch.Count < BatchSize && Environment.TickCount64 < flushAt) continue;
                state = await SaveAsync(context, profile, batch, fresh, state, progress, cancellationToken).ConfigureAwait(false);
                reported = state.Processed;
            }
            if (state.Processed != reported)
                state = await SaveAsync(context, profile, batch, fresh, state, progress, cancellationToken).ConfigureAwait(false);
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

    private async Task<SemanticSnapshot> SaveAsync(ScanRunContext context, EncoderProfile profile,
        List<StoredEmbedding> batch, List<StoredSignature> fresh, SemanticSnapshot state,
        Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken)
    {
        if (batch.Count != 0)
        {
            await store.AddAsync(context.Pin.ScanId, profile, batch, cancellationToken).ConfigureAwait(false);
            state = state with { Indexed = state.Indexed + batch.Count };
            batch.Clear();
        }
        if (fresh.Count != 0)
        {
            try
            {
                // Not canceled: the batch's vectors are already in the index and counted, and a cancel
                // here would return the count from before them. It is one small transaction.
                await signatures.AddAsync(fresh, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The index has the vectors; only the next Scan encodes these photos again.
                state = state with
                {
                    Vectors = state.Vectors with
                    {
                        Misses = state.Vectors.Misses - fresh.Count,
                        Rejects = state.Vectors.Rejects + fresh.Count,
                    },
                };
            }
            fresh.Clear();
        }
        await progress(state).ConfigureAwait(false);
        return state;
    }

    // True when an item is ready, false when the workers are done, null when the wait ran out.
    private static async Task<bool?> WaitToReadAsync(ChannelReader<Encoded> reader, int milliseconds,
        CancellationToken cancellationToken)
    {
        if (milliseconds == Timeout.Infinite)
            return await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        if (milliseconds == 0) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(milliseconds);
        try
        {
            return await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
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
        ImmutableArray<ScanFileOccurrence> eligible, Channel<Work> work,
        ChannelWriter<Encoded> done, CancellationToken token)
    {
        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(() => WorkAsync(context, profile, work.Reader, done, token), CancellationToken.None))
            .Append(ProduceAsync(profile, eligible, work.Writer, token))
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

    // Stored vectors are looked up one batch at a time: a vector is about 3 KB.
    private async Task ProduceAsync(EncoderProfile profile, ImmutableArray<ScanFileOccurrence> eligible,
        ChannelWriter<Work> writer, CancellationToken token)
    {
        try
        {
            foreach (var chunk in eligible.Chunk(BatchSize))
            {
                var stored = await LookupAsync(profile, chunk, token).ConfigureAwait(false);
                foreach (var row in chunk)
                    await writer.WriteAsync(Find(profile, row, stored), token).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    // A lookup that fails makes its photos rejects: they are encoded as if nothing were stored.
    private async Task<IReadOnlyDictionary<SignatureKey, byte[]?>?> LookupAsync(EncoderProfile profile,
        ScanFileOccurrence[] rows, CancellationToken token)
    {
        try
        {
            return await signatures.LookupAsync([.. rows.Select(row => Key(row, profile)).Distinct()], token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    private static Work Find(EncoderProfile profile, ScanFileOccurrence row,
        IReadOnlyDictionary<SignatureKey, byte[]?>? stored)
    {
        if (stored is null) return new(row, Found.Reject, null);
        if (!stored.TryGetValue(Key(row, profile), out var value)) return new(row, Found.Miss, null);
        return SignatureCodec.TryDecodeVector(value, profile.Dimension, out var vector)
            ? new(row, Found.Hit, vector)
            : new(row, Found.Reject, null);
    }

    // The vector's revision is the profile it was made under; the content is the one the Exact stage proved.
    private static SignatureKey Key(ScanFileOccurrence row, EncoderProfile profile) =>
        new(row.Content!.Sha256, row.Content.ByteLength, SignatureKind.Vector, profile.Fingerprint);

    private static SignatureReuseCounts Count(SignatureReuseCounts counts, Found found) => found switch
    {
        Found.Hit => counts with { Hits = counts.Hits + 1 },
        Found.Miss => counts with { Misses = counts.Misses + 1 },
        _ => counts with { Rejects = counts.Rejects + 1 },
    };

    private async Task WorkAsync(ScanRunContext context, EncoderProfile profile,
        ChannelReader<Work> reader, ChannelWriter<Encoded> done, CancellationToken token)
    {
        await foreach (var (row, found, stored) in reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (stored is not null)
            {
                await done.WriteAsync(new(row, stored, null, found), token).ConfigureAwait(false);
                continue;
            }
            EncoderImage? image;
            try
            {
                image = await images.ReadAsync(context, row, profile.DecodeEdge, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // A photo that cannot be read or decoded is skipped; the snapshot discloses the gap.
                image = null;
            }
            Encoded result;
            try
            {
                result = image is null ? new(row, null, null, found) : Encode(profile, row, image, found);
            }
            catch (Exception)
            {
                result = new(row, null, new(EncoderFailureKind.Failed, "Preparing a photo for search failed unexpectedly."), found);
            }
            await done.WriteAsync(result, token).ConfigureAwait(false);
        }
    }

    private Encoded Encode(EncoderProfile profile, ScanFileOccurrence row, EncoderImage image, Found found)
    {
        var embedding = encoder.EncodeImage(profile, image);
        return embedding.Failure is { } failure
            ? new(row, null, failure, found)
            : new(row, embedding.Vector.ToArray(), null, found);
    }

    /// <summary>Hit: the stored vector decoded. Miss: none stored. Reject: no lookup, or a vector that cannot be true.</summary>
    private enum Found
    {
        Hit,
        Miss,
        Reject,
    }

    private sealed record Work(ScanFileOccurrence Row, Found Found, float[]? Stored);

    private sealed record Encoded(ScanFileOccurrence Row, float[]? Vector, EncoderFailure? Failure, Found Found);
}
