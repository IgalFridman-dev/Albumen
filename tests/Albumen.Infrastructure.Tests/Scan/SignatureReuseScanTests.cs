using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Analysis.Exact;
using Albumen.Analysis.NearDuplicate;
using Albumen.Analysis.Sharpness;
using Albumen.Application.LocalData;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Application.Shell;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Onnx;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Tests.Exact;
using Albumen.Infrastructure.Windows.Preview;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Albumen.Infrastructure.Tests.Scan;

/// <summary>
/// E2-S4: one folder scanned more than once through the real stages (Windows reads, WIC, the
/// managed analyzers, ONNX and SQLite), each Scan a fresh coordinator over the same database.
/// </summary>
public sealed class SignatureReuseScanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SignatureReuse Every(long hits, long misses) => new(new(hits, misses, 0), new(hits, misses, 0), new(hits, misses, 0));

    [Fact]
    public async Task ASecondScanReusesEveryFeatureAndOpensNoPhotoInTheFeatureStages()
    {
        await using var folder = ReuseFolder.Create();
        folder.WriteSet();

        var cold = await folder.ScanAsync();
        var warm = await folder.ScanAsync();

        Assert.Equal((ScanStatus.Complete, Every(0, 5), 5, 5), (cold.Status, cold.Reuse, cold.NearOpens, cold.IndexOpens));
        Assert.Equal((ScanStatus.Complete, Every(5, 0), 0, 0), (warm.Status, warm.Reuse, warm.NearOpens, warm.IndexOpens));
        Assert.Contains("Exact: a.png, copy.png", cold.Groups);
        Assert.Contains(cold.Groups, group => group.Contains("twin.png", StringComparison.Ordinal)
            && !group.StartsWith("Exact", StringComparison.Ordinal));
        Assert.Equal(cold.Groups, warm.Groups);
        Assert.Null(cold.Sharpness["small-1.png"]);
        Assert.NotNull(cold.Sharpness["a.png"]);
        Assert.Equal(cold.Sharpness, warm.Sharpness);
        AssertSameVectors(folder, cold, warm, 5);
        // One row per content and kind: the identical copies share theirs.
        Assert.Equal(12, await folder.SignatureRowsAsync());
    }

    [Fact]
    public async Task AChangedFileAndANewFileAtAnOldPathMiss()
    {
        await using var folder = ReuseFolder.Create();
        folder.WriteSet();
        await folder.ScanAsync();
        // The same pixels at another DPI: other bytes, the same length, and the old timestamp put back.
        var original = File.ReadAllBytes(folder.PathOf("a.png"));
        var replacement = CreatePng(1, 1200, 900, dpi: 72);
        Assert.Equal(original.Length, replacement.Length);
        Assert.NotEqual(original, replacement);
        var written = File.GetLastWriteTimeUtc(folder.PathOf("a.png"));
        folder.Write("a.png", replacement);
        File.SetLastWriteTimeUtc(folder.PathOf("a.png"), written);
        Assert.Equal(written, File.GetLastWriteTimeUtc(folder.PathOf("a.png")));
        File.Delete(folder.PathOf("small-2.png"));
        folder.Write("small-2.png", CreatePng(6, 40, 30));

        var changed = await folder.ScanAsync();
        var after = await folder.ScanAsync();

        Assert.Equal((Every(3, 2), 2, 2), (changed.Reuse, changed.NearOpens, changed.IndexOpens));
        Assert.DoesNotContain("Exact: a.png, copy.png", changed.Groups);
        // The changed photos' new results were stored.
        Assert.Equal((Every(5, 0), 0, 0), (after.Reuse, after.NearOpens, after.IndexOpens));
        Assert.Equal(changed.Groups, after.Groups);
        Assert.Equal(changed.Sharpness, after.Sharpness);
        AssertSameVectors(folder, changed, after, 5);
    }

    [Fact]
    public async Task AMovedAndACopiedIdenticalFileHitAndStayTheirOwnOccurrence()
    {
        await using var folder = ReuseFolder.Create();
        folder.WriteSet();
        await folder.ScanAsync();
        Directory.CreateDirectory(folder.PathOf("moved"));
        File.Move(folder.PathOf("copy.png"), folder.PathOf("moved/copy.png"));
        File.Copy(folder.PathOf("a.png"), folder.PathOf("a (2).png"));

        var again = await folder.ScanAsync();

        Assert.Equal((Every(6, 0), 0, 0), (again.Reuse, again.NearOpens, again.IndexOpens));
        Assert.Contains("Exact: a (2).png, a.png, moved/copy.png", again.Groups);
        Assert.Equal(6, again.Vectors.Count);
        Assert.Equal(again.Vectors["a.png"], again.Vectors["a (2).png"]);
    }

    [Fact]
    public async Task ClearScanDataMakesTheNextScanCold()
    {
        await using var folder = ReuseFolder.Create();
        folder.WriteSet();
        var first = await folder.ScanAsync();

        Assert.Equal(ScanDataClearOutcome.Cleared, await folder.ClearScanDataAsync());
        Assert.Equal(0, await folder.SignatureRowsAsync());
        var cleared = await folder.ScanAsync();

        Assert.Equal((Every(0, 5), 5, 5), (cleared.Reuse, cleared.NearOpens, cleared.IndexOpens));
        Assert.Equal(first.Groups, cleared.Groups);
        Assert.Equal(12, await folder.SignatureRowsAsync());
    }

    /// <summary>
    /// The shipped model over 200 synthetic camera-sized photos, scanned cold then warm. Records the
    /// times next to hashing alone and the database size; asserts only reuse and completeness.
    /// </summary>
    [Fact]
    [Trait("Category", "GoldenVm")]
    public async Task ASecondScanOfTwoHundredPhotosIsAboutTheCostOfHashing()
    {
        const int Photos = 200;
        var models = Environment.GetEnvironmentVariable("ALBUMEN_MODELS_DIR") is { Length: > 0 } configured
            ? configured
            : @"C:\PhotoSift\models";
        await using var folder = ReuseFolder.Create(models);
        for (var id = 1; id <= Photos; id++)
            folder.Write($"{id.ToString("D3", CultureInfo.InvariantCulture)}.png", CreatePng(id, 1600, 1200));

        var cold = await folder.ScanAsync();
        var (databaseBytes, signatureBytes) = await folder.MeasureDatabaseAsync();
        var warm = await folder.ScanAsync();
        var hashing = await folder.HashEveryFileAsync();

        Assert.Equal((ScanStatus.Complete, Every(0, Photos)), (cold.Status, cold.Reuse));
        Assert.Equal((ScanStatus.Complete, Every(Photos, 0), 0, 0), (warm.Status, warm.Reuse, warm.NearOpens, warm.IndexOpens));
        AssertSameVectors(folder, cold, warm, Photos);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"signature-reuse: photos={Photos} cold-ms={cold.Elapsed.TotalMilliseconds:F0} " +
            $"warm-ms={warm.Elapsed.TotalMilliseconds:F0} hash-only-ms={hashing.TotalMilliseconds:F0} " +
            $"db-bytes-per-1000={databaseBytes * 1000 / Photos} signature-bytes-per-1000={signatureBytes * 1000 / Photos}");
    }

    // Parallel ONNX runs can differ in the last float bit for identical bytes, and identical copies
    // share the one vector stored first, so a photo's vector is the one computed for any photo with
    // its content.
    private static void AssertSameVectors(ReuseFolder folder, ScanResult expected, ScanResult actual, int count)
    {
        Assert.Equal(count, expected.Vectors.Count);
        Assert.Equal(expected.Vectors.Keys.Order(StringComparer.Ordinal), actual.Vectors.Keys.Order(StringComparer.Ordinal));
        var content = expected.Vectors.Keys.ToDictionary(key => key,
            key => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(folder.PathOf(key)))));
        Assert.All(actual.Vectors, vector => Assert.Contains(expected.Vectors,
            candidate => content[candidate.Key] == content[vector.Key] && candidate.Value.SequenceEqual(vector.Value)));
    }

    // A different gradient per seed; a twin is the seed-1 photo with one row changed.
    private static byte[] CreatePng(int seed, int width, int height, bool twin = false, double dpi = 96)
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
        if (twin) Array.Fill(pixels, (byte)255, height / 2 * width * 4, width * 4);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32,
            null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record ScanResult(
        ScanStatus Status,
        SignatureReuse Reuse,
        int NearOpens,
        int IndexOpens,
        ImmutableArray<string> Groups,
        IReadOnlyDictionary<string, double?> Sharpness,
        IReadOnlyDictionary<string, float[]> Vectors,
        TimeSpan Elapsed);

    private sealed class ReuseFolder : IAsyncDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("albumen-reuse-").FullName;
        private readonly string? copiedModels;
        private readonly OnnxVisionLanguageEncoder encoder;
        private readonly EncoderProfile profile;

        private ReuseFolder(string models, bool copied)
        {
            copiedModels = copied ? models : null;
            Directory.CreateDirectory(Photos);
            encoder = new OnnxVisionLanguageEncoder(models);
            profile = encoder.LoadProfile().Profile!;
        }

        private string Photos => Path.Combine(root, "photos");
        private string DatabasePath => Path.Combine(root, "albumen.db");

        // The test models by default, copied so the folder can go with the test.
        public static ReuseFolder Create(string? models = null)
        {
            if (models is not null) return new ReuseFolder(models, copied: false);
            var copy = Directory.CreateTempSubdirectory("albumen-models-").FullName;
            foreach (var name in new[] { "manifest.json", "vision.onnx", "text.onnx", "tokenizer.model" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Onnx", "Fixtures", name), Path.Combine(copy, name));
            return new ReuseFolder(copy, copied: true);
        }

        public string PathOf(string relative) => Path.Combine(Photos, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, byte[] content) => File.WriteAllBytes(PathOf(relative), content);

        // Two identical photos, a near twin of them, two small photos without a sharpness score,
        // and a file that is not a photo.
        public void WriteSet()
        {
            var photo = CreatePng(1, 1200, 900);
            Write("a.png", photo);
            Write("copy.png", photo);
            Write("twin.png", CreatePng(1, 1200, 900, twin: true));
            Write("small-1.png", CreatePng(3, 40, 30));
            Write("small-2.png", CreatePng(4, 40, 30));
            File.WriteAllText(PathOf("notes.txt"), "not a photo");
        }

        public async Task<ScanResult> ScanAsync()
        {
            var preflight = new WindowsLocalScopePreflight();
            var selection = new ScopeSelection(Photos, ScopeRecursion.IncludeSubfolders);
            var checkedScope = ScopePreflightPolicy.Evaluate(await preflight.InspectAsync(selection, Token));
            Assert.Equal(ScopePreflightDecision.Ready, checkedScope.Decision);
            var analyzer = new StreamingExactContentAnalyzer();
            var nearReader = new CountingReader(new WindowsProtectedFileReader());
            var indexReader = new CountingReader(new WindowsProtectedFileReader());
            var scheduler = new SerialTestScheduler();
            var stores = new SqliteAlbumenStores(DatabasePath);
            var coordinator = new ScanCoordinator(scheduler, preflight,
                new BoundedScanExecutor(new WindowsScanEnumerator(), new WindowsProtectedFileReader(), analyzer),
                stores.ScanIdentities, stores.ScanCheckpoints,
                new BoundedNearDuplicatePipeline(new WicNearFeatureSource(nearReader, analyzer, new WicPreviewDecoder(),
                    new ManagedPerceptualSignature(), new ManagedSharpness()), stores.Signatures),
                new BoundedSemanticIndexer(encoder, new WicEncoderImageSource(indexReader, analyzer, new WicPreviewDecoder()),
                    stores.Embeddings, stores.Signatures, 2),
                new StoredEmbeddingMatcher(encoder, stores.Embeddings));
            var terminal = new TaskCompletionSource<ScanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.Status is ScanStatus.Complete or ScanStatus.Incomplete or ScanStatus.Faulted)
                    terminal.TrySetResult(snapshot);
            };
            try
            {
                var ready = new ShellSnapshot([], "Reuse folder ready", ScopeSessionState.Ready, selection, checkedScope, Revision: 1);
                var clock = Stopwatch.StartNew();
                await coordinator.StartAsync(ready, new ExactRuleConfirmation(ready.Revision, 1), Token);
                var snapshot = await terminal.Task.WaitAsync(TimeSpan.FromMinutes(10), Token);
                clock.Stop();
                var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
                var paths = snapshot.Occurrences.ToDictionary(row => row.Token.OccurrenceId, row => Relative(row.DiscoveredPath));
                await stores.Embeddings.ReadAsync(snapshot.Pin!.ScanId, profile,
                    embedding => vectors.Add(paths[embedding.OccurrenceId], embedding.Vector), Token);
                return new(snapshot.Status, snapshot.Reuse, nearReader.Opens, indexReader.Opens,
                    [.. snapshot.Groups.Select(group => Describe(group, paths)).Order(StringComparer.Ordinal)],
                    snapshot.Occurrences.Where(row => row.Content is not null)
                        .ToDictionary(row => Relative(row.DiscoveredPath), row => row.Sharpness, StringComparer.Ordinal),
                    vectors, clock.Elapsed);
            }
            finally
            {
                await coordinator.DisposeAsync();
                await scheduler.DisposeAsync();
                await stores.DisposeAsync();
            }
        }

        public async Task<ScanDataClearOutcome> ClearScanDataAsync()
        {
            await using var stores = new SqliteAlbumenStores(DatabasePath);
            return (await stores.ScanData.ClearAsync(Token)).Outcome;
        }

        public async Task<long> SignatureRowsAsync()
        {
            await using var connection = await OpenAsync(DatabasePath);
            return await ScalarAsync(connection, "SELECT COUNT(*) FROM signatures;");
        }

        // The whole database and the part the stored signatures take, both compacted.
        public async Task<(long Database, long Signatures)> MeasureDatabaseAsync()
        {
            var compacted = Path.Combine(root, "compacted.db");
            await using (var connection = await OpenAsync(DatabasePath))
                await ScalarAsync(connection, $"VACUUM INTO '{compacted}';");
            var database = new FileInfo(compacted).Length;
            await using (var connection = await OpenAsync(compacted))
                await ScalarAsync(connection, "DELETE FROM signatures; VACUUM;");
            SqliteConnection.ClearAllPools();
            var signatures = database - new FileInfo(compacted).Length;
            File.Delete(compacted);
            return (database, signatures);
        }

        public async Task<TimeSpan> HashEveryFileAsync()
        {
            var analyzer = new StreamingExactContentAnalyzer();
            var clock = Stopwatch.StartNew();
            foreach (var path in Directory.EnumerateFiles(Photos, "*.png", SearchOption.AllDirectories))
            {
                await using var stream = File.OpenRead(path);
                await analyzer.HashAsync(stream, stream.Length, Token);
            }
            return clock.Elapsed;
        }

        public ValueTask DisposeAsync()
        {
            // A Ready index opens the text model in the background; wait for it, so the model
            // files are closed before the folders go.
            Assert.Null(encoder.PrepareText(profile));
            encoder.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            if (copiedModels is not null) Directory.Delete(copiedModels, recursive: true);
            return ValueTask.CompletedTask;
        }

        private string Relative(string path) => Path.GetRelativePath(Photos, path).Replace(Path.DirectorySeparatorChar, '/');

        // Category, members and the similarity evidence: equal only when every signature is.
        private static string Describe(CandidateGroup group, Dictionary<long, string> paths)
        {
            var members = string.Join(", ", group.Members.Select(member => paths[member.Token.OccurrenceId]).Order(StringComparer.Ordinal));
            var similarity = group.Similarity
                .Select(pair => string.Create(CultureInfo.InvariantCulture,
                    $" [{paths[pair.Left.OccurrenceId]}~{paths[pair.Right.OccurrenceId]} {pair.Kind} "
                    + $"h{pair.HammingDistance} c{pair.ColorDistance} {pair.Cosine:R}]"))
                .Order(StringComparer.Ordinal);
            return $"{group.Category}: {members}{string.Concat(similarity)}";
        }

        private static async Task<SqliteConnection> OpenAsync(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync(Token);
            return connection;
        }

        private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(Token) is long value ? value : 0;
        }
    }

    private sealed class CountingReader(IProtectedFileReader inner) : IProtectedFileReader
    {
        private int opens;

        public int Opens => Volatile.Read(ref opens);

        public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            return inner.OpenAsync(request, cancellationToken);
        }
    }
}
