using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Analysis.Exact;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Onnx;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Tests.Sqlite;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Infrastructure.Tests.Onnx;

/// <summary>The indexer with the ONNX adapter, the WIC decode and the SQLite store together.</summary>
public sealed class SemanticIndexTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AScanIsIndexedIntoTheStoreUnderTheInstalledProfile()
    {
        var models = Directory.CreateTempSubdirectory("albumen-models-").FullName;
        try
        {
            foreach (var name in new[] { "manifest.json", "vision.onnx", "text.onnx", "tokenizer.model" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Onnx", "Fixtures", name), Path.Combine(models, name));
            using var encoder = new OnnxVisionLanguageEncoder(models);
            await using var scan = await PhotoScan.CreateAsync(5, 300, 200);
            var changed = scan.Rows[2];
            var rows = scan.Rows.SetItem(2, new ScanFileOccurrence(changed.Token, changed.DiscoveredPath,
                changed.ResolvedPath, changed.PhysicalFileKey, changed.Enumerate, changed.Exact,
                new(changed.PhysicalFileKey!.Value, changed.Content!.ByteLength, new string('0', 64)), changed.Allocation));

            var result = await scan.IndexAsync(encoder, rows, workers: 2, Token);

            var profile = encoder.LoadProfile().Profile!;
            Assert.Equal(new SemanticSnapshot(SemanticState.Ready, 5, 5, 4, profile.Fingerprint, ""), result);
            var stored = await scan.ReadAsync(profile);
            Assert.Equal([1L, 2L, 4L, 5L], stored.Select(embedding => embedding.OccurrenceId));
            Assert.All(stored, embedding => Assert.Equal(1.0, Norm(embedding.Vector), 5));
            Assert.Empty(await scan.ReadAsync(profile with { PipelineRevision = profile.PipelineRevision + 1 }));
        }
        finally
        {
            Directory.Delete(models, recursive: true);
        }
    }

    /// <summary>
    /// The shipped model over a scan of synthetic camera-sized photos. Records ms per photo and the
    /// working set; asserts only completeness and the 4 GiB the Golden VM has.
    /// </summary>
    [Fact]
    [Trait("Category", "GoldenVm")]
    public async Task TheShippedModelIndexesALargeScanAndCancelKeepsAConsistentPartialIndex()
    {
        var models = Environment.GetEnvironmentVariable("ALBUMEN_MODELS_DIR") is { Length: > 0 } configured
            ? configured
            : @"C:\PhotoSift\models";
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;
        const int Photos = 200;
        await using var scan = await PhotoScan.CreateAsync(Photos, 1600, 1200);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var peak = workingSetBefore;
        using var sampling = new Timer(_ =>
        {
            process.Refresh();
            peak = Math.Max(peak, process.WorkingSet64);
        }, null, 0, 50);

        var clock = Stopwatch.StartNew();
        var result = await scan.IndexAsync(encoder, scan.Rows, Environment.ProcessorCount, Token);
        clock.Stop();

        Assert.Equal((SemanticState.Ready, Photos), (result.State, result.Indexed));
        var stored = await scan.ReadAsync(profile);
        Assert.Equal(Photos, stored.Count);
        Assert.All(stored, embedding =>
        {
            Assert.Equal(profile.Dimension, embedding.Vector.Length);
            Assert.Equal(1.0, Norm(embedding.Vector), 4);
        });
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"semantic-index: photos={Photos} workers={Environment.ProcessorCount} " +
            $"ms-per-photo={clock.Elapsed.TotalMilliseconds / Photos:F1} " +
            $"working-set-before-mb={workingSetBefore / 1048576} peak-working-set-mb={peak / 1048576}");
        Assert.InRange(peak, 0, 3L * 1024 * 1024 * 1024);

        using var cancel = new CancellationTokenSource();
        var partial = await scan.IndexAsync(encoder, scan.Rows, Environment.ProcessorCount, cancel.Token,
            progress =>
            {
                if (progress.Indexed >= Photos / 3) cancel.Cancel();
            });

        Assert.Equal(SemanticState.Ready, partial.State);
        Assert.InRange(partial.Indexed, Photos / 3, Photos - 1);
        Assert.Equal(partial.Indexed, (await scan.ReadAsync(profile)).Count);
    }

    private static double Norm(float[] vector) => Math.Sqrt(vector.Sum(value => (double)value * value));

    private sealed class PhotoScan : IAsyncDisposable
    {
        private readonly ScanIdentityFixture database = new();
        private readonly Dictionary<string, byte[]> files = [];
        private SqliteAlbumenStores stores = null!;

        public ImmutableArray<ScanFileOccurrence> Rows { get; private set; } = [];
        private ScanRunContext Context { get; set; } = null!;

        public static async Task<PhotoScan> CreateAsync(int count, int width, int height)
        {
            var scan = new PhotoScan();
            scan.stores = new SqliteAlbumenStores(scan.database.DatabasePath);
            var identity = ScanIdentityData.Scan();
            scan.Context = new(identity.Pin, new(identity.Selection, identity.ResolvedPath, identity.RootIdentity));
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await scan.stores.ScanIdentities.PersistScanAsync(identity, Token)).Outcome);
            var rows = ImmutableArray.CreateBuilder<ScanFileOccurrence>(count);
            for (var id = 1; id <= count; id++)
            {
                var path = $@"C:\photos\{id}.png";
                var content = CreatePng(id, width, height);
                scan.files[path] = content;
                var key = new PhysicalFileKey(7, new(0, (ulong)id));
                var occurrence = ScanIdentityData.Occurrence(identity.Pin, id, path, path, key);
                Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                    (await scan.stores.ScanIdentities.PersistOccurrenceAsync(occurrence, Token)).Outcome);
                rows.Add(new(occurrence.Token, path, path, key, new(ScanStageState.Succeeded, ScanReason.None),
                    new(ScanStageState.Succeeded, ScanReason.None),
                    new(key, content.LongLength, Convert.ToHexString(SHA256.HashData(content))),
                    new(content.LongLength, 1, true)));
            }

            scan.Rows = rows.MoveToImmutable();
            return scan;
        }

        public Task<SemanticSnapshot> IndexAsync(IVisionLanguageEncoder encoder, ImmutableArray<ScanFileOccurrence> rows,
            int workers, CancellationToken cancellationToken, Action<SemanticSnapshot>? progress = null) =>
            new BoundedSemanticIndexer(encoder,
                    new WicEncoderImageSource(new MemoryReader(files), new StreamingExactContentAnalyzer(), new WicPreviewDecoder()),
                    stores.Embeddings, workers)
                .IndexAsync(Context, rows, snapshot =>
                {
                    progress?.Invoke(snapshot);
                    return Task.CompletedTask;
                }, cancellationToken);

        public async Task<List<StoredEmbedding>> ReadAsync(EncoderProfile profile)
        {
            var read = new List<StoredEmbedding>();
            await stores.Embeddings.ReadAsync(Context.Pin.ScanId, profile, read.Add, Token);
            return read;
        }

        public async ValueTask DisposeAsync()
        {
            await stores.DisposeAsync();
            await database.DisposeAsync();
        }

        // A different smooth gradient per photo, so PNGs stay small and vectors differ.
        private static byte[] CreatePng(int seed, int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = ((y * width) + x) * 4;
                    pixels[i] = (byte)(x * seed / 16);
                    pixels[i + 1] = (byte)(y * seed / 16);
                    pixels[i + 2] = (byte)((x + y) / (seed + 1));
                    pixels[i + 3] = 255;
                }
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, pixels, width * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    private sealed class MemoryReader(Dictionary<string, byte[]> files) : IProtectedFileReader
    {
        public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken)
        {
            var content = files[request.ResolvedPath];
            return ValueTask.FromResult<IProtectedFileLease>(new MemoryLease(new MemoryStream(content, writable: false),
                new(request.ExpectedPhysicalFileKey, content.LongLength, request.ResolvedPath, DateTimeOffset.UnixEpoch,
                    new(content.LongLength, 1, true))));
        }
    }

    private sealed class MemoryLease(Stream stream, ProtectedFileFacts facts) : IProtectedFileLease
    {
        public Stream Content { get; } = stream;
        public ProtectedFileFacts OpenedFacts { get; } = facts;
        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(OpenedFacts);
        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }
}
