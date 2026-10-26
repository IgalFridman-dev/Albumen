using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

[Flags]
public enum NearFeatures
{
    None = 0,
    Signature = 1,
    Sharpness = 2,
    All = Signature | Sharpness,
}

public interface INearFeatureSource
{
    /// <summary>Computes the wanted features from one open of the file; the others stay null.</summary>
    ValueTask<NearFeatureResult> AnalyzeAsync(
        ScanRunContext context, ScanFileOccurrence row, NearFeatures wanted, CancellationToken cancellationToken);
}

public interface IPerceptualSignatureAnalyzer
{
    PerceptualSignature Extract(int width, int height, ReadOnlySpan<byte> bgra);
}

public interface ISharpnessAnalyzer
{
    double Analyze(int width, int height, ReadOnlySpan<byte> bgra);
}

public interface INearDuplicatePipeline
{
    Task<NearDuplicateBatch> AnalyzeAsync(ScanRunContext context,
        ImmutableArray<ScanFileOccurrence> rows, CancellationToken cancellationToken);
}

/// <summary>
/// Visual features of every photo, read one at a time. A feature stored under the photo's proven
/// content and the current revision is reused without opening the photo; only the missing ones are
/// computed, and those are stored for the next Scan.
/// </summary>
public sealed class BoundedNearDuplicatePipeline(INearFeatureSource source, ISignatureStore signatures)
    : INearDuplicatePipeline
{
    public const int Capacity = 16;

    /// <summary>Most photos whose new features are written in one transaction.</summary>
    public const int SignatureBatchSize = 64;

    // A stored feature is reused only under the revision it was made with. Each revision covers the
    // analyzer and the decode it sees (WicNearFeatureSource: 256 px for the dHash, 1024 px for
    // sharpness, EXIF-oriented, WIC-scaled BGRA32): change either and bump it.
    public const string ImplementationRevision = "managed-dhash-bgra-v1";
    public const string SharpnessRevision = "managed-laplacian-bgra-1024-v1";

    private static readonly StageOutcome Succeeded = new(ScanStageState.Succeeded, ScanReason.None);

    public async Task<NearDuplicateBatch> AnalyzeAsync(ScanRunContext context,
        ImmutableArray<ScanFileOccurrence> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stored = await LookupAsync(rows, cancellationToken).ConfigureAwait(false);
        var channel = Channel.CreateBounded<ScanFileOccurrence>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        var results = ImmutableArray.CreateBuilder<NearFeatureResult>(rows.Length);
        var work = new Work(stored);
        var producer = ProduceAsync(channel.Writer, rows, cancellationToken);
        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(await AnalyzeAsync(context, row, work, cancellationToken).ConfigureAwait(false));
            if (work.FreshPhotos == SignatureBatchSize) await WriteAsync(work, cancellationToken).ConfigureAwait(false);
        }
        await producer.ConfigureAwait(false);
        await WriteAsync(work, cancellationToken).ConfigureAwait(false);
        return new NearDuplicateBatch(context.Pin, ImplementationRevision, results.ToImmutable())
        {
            Reuse = new(work.Near.Counts, work.Sharpness.Counts, SignatureReuseCounts.None),
        };
    }

    private async Task<NearFeatureResult> AnalyzeAsync(ScanRunContext context, ScanFileOccurrence row, Work work,
        CancellationToken cancellationToken)
    {
        if (!Proven(row, out var content))
            return await ComputeAsync(context, row, NearFeatures.All, cancellationToken).ConfigureAwait(false);
        PerceptualSignature? storedSignature = null;
        double? storedSharpness = null;
        var signatureKey = Key(content, SignatureKind.Near);
        var sharpnessKey = Key(content, SignatureKind.Sharpness);
        var signature = work.Near.Count(work.Stored, signatureKey,
            value => SignatureCodec.TryDecodeNear(value, ImplementationRevision, out storedSignature));
        var sharpness = work.Sharpness.Count(work.Stored, sharpnessKey,
            value => SignatureCodec.TryDecodeSharpness(value, out storedSharpness));
        var wanted = (signature == Found.Hit ? NearFeatures.None : NearFeatures.Signature)
            | (sharpness == Found.Hit ? NearFeatures.None : NearFeatures.Sharpness);
        // A full hit is the result a computation would give, so grouping cannot tell them apart.
        if (wanted == NearFeatures.None) return new(row.Token, content, storedSignature, Succeeded, storedSharpness);

        var computed = await ComputeAsync(context, row, wanted, cancellationToken).ConfigureAwait(false);
        if (computed.Outcome.State != ScanStageState.Succeeded) return computed;
        // Only what was missing is stored; a rejected row keeps what is stored, so it is not rewritten.
        var before = work.Fresh.Count;
        if (signature == Found.Miss && computed.Signature is { } made)
            work.Fresh.Add(new(signatureKey, SignatureCodec.EncodeNear(made)));
        if (sharpness == Found.Miss)
            work.Fresh.Add(new(sharpnessKey, SignatureCodec.EncodeSharpness(computed.Sharpness)));
        if (work.Fresh.Count != before) work.FreshPhotos++;
        return computed with
        {
            Signature = wanted.HasFlag(NearFeatures.Signature) ? computed.Signature : storedSignature,
            Sharpness = wanted.HasFlag(NearFeatures.Sharpness) ? computed.Sharpness : storedSharpness,
        };
    }

    private async Task<NearFeatureResult> ComputeAsync(ScanRunContext context, ScanFileOccurrence row,
        NearFeatures wanted, CancellationToken cancellationToken)
    {
        try
        {
            return await source.AnalyzeAsync(context, row, wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var outcome = exception is IOException or UnauthorizedAccessException
                ? new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure)
                : new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault);
            return new NearFeatureResult(row.Token, row.Content, null, outcome);
        }
    }

    // A lookup that fails makes every photo a reject: it is computed as if nothing were stored.
    private async Task<IReadOnlyDictionary<SignatureKey, byte[]?>?> LookupAsync(
        ImmutableArray<ScanFileOccurrence> rows, CancellationToken cancellationToken)
    {
        var keys = new HashSet<SignatureKey>();
        foreach (var row in rows)
        {
            if (!Proven(row, out var content)) continue;
            keys.Add(Key(content, SignatureKind.Near));
            keys.Add(Key(content, SignatureKind.Sharpness));
        }
        try
        {
            return await signatures.LookupAsync(keys, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    // A write that fails changes no result: its photos were computed all the same and count as
    // rejects, and the next Scan computes them again.
    private async Task WriteAsync(Work work, CancellationToken cancellationToken)
    {
        if (work.Fresh.Count == 0) return;
        try
        {
            await signatures.AddAsync(work.Fresh, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            work.Near.WriteFailed(work.Fresh.Count(signature => signature.Key.Kind == SignatureKind.Near));
            work.Sharpness.WriteFailed(work.Fresh.Count(signature => signature.Key.Kind == SignatureKind.Sharpness));
        }
        work.Fresh.Clear();
        work.FreshPhotos = 0;
    }

    // The proof of identity is the SHA-256 and length the Exact stage took under its lease.
    private static bool Proven(ScanFileOccurrence row, [NotNullWhen(true)] out ContentRevision? content)
    {
        content = row.Exact.State == ScanStageState.Succeeded ? row.Content : null;
        return content is not null;
    }

    private static SignatureKey Key(ContentRevision content, SignatureKind kind) => new(content.Sha256,
        content.ByteLength, kind, kind == SignatureKind.Near ? ImplementationRevision : SharpnessRevision);

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

    private enum Found
    {
        Hit,
        Miss,
        Reject,
    }

    private sealed class Work(IReadOnlyDictionary<SignatureKey, byte[]?>? stored)
    {
        public IReadOnlyDictionary<SignatureKey, byte[]?>? Stored { get; } = stored;
        public Tally Near { get; } = new();
        public Tally Sharpness { get; } = new();
        public List<StoredSignature> Fresh { get; } = [];
        public int FreshPhotos { get; set; }
    }

    private sealed class Tally
    {
        private long hits;
        private long misses;
        private long rejects;

        public SignatureReuseCounts Counts => new(hits, misses, rejects);

        // Hit: the stored value decoded. Miss: nothing stored. Reject: no lookup, or a value that cannot be true.
        public Found Count(IReadOnlyDictionary<SignatureKey, byte[]?>? stored, SignatureKey key, Func<byte[]?, bool> decode)
        {
            if (stored is not null && stored.TryGetValue(key, out var value) && decode(value)) { hits++; return Found.Hit; }
            if (stored is not null && !stored.ContainsKey(key)) { misses++; return Found.Miss; }
            rejects++;
            return Found.Reject;
        }

        public void WriteFailed(int count)
        {
            misses -= count;
            rejects += count;
        }
    }
}
