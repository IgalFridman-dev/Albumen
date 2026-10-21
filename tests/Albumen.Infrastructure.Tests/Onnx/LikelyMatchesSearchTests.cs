using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Onnx;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Tests.Sqlite;
using Xunit;

namespace Albumen.Infrastructure.Tests.Onnx;

/// <summary>
/// The search query with the shipped text model and the SQLite store over a 10,000-photo Scan.
/// Records ms per prompt; asserts only the ranking. Reads the model like OnnxEncoderParityTests.
/// </summary>
[Trait("Category", "GoldenVm")]
public sealed class LikelyMatchesSearchTests
{
    private const int Photos = 10_000;
    private const long Target = 4_321;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheShippedModelRanksTenThousandStoredPhotosAndARepeatedPromptGivesTheSameOrder()
    {
        var models = Environment.GetEnvironmentVariable("ALBUMEN_MODELS_DIR") is { Length: > 0 } configured
            ? configured
            : @"C:\PhotoSift\models";
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;
        await using var database = new ScanIdentityFixture();
        await using var stores = new SqliteAlbumenStores(database.DatabasePath);
        var identity = ScanIdentityData.Scan();
        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(identity, Token)).Outcome);

        // Random photos, except that one photo's vector is the prompt's own: it must rank first.
        const string Prompt = "a boat at sea";
        var target = encoder.EncodeText(profile, Prompt).Vector.ToArray();
        var random = new Random(20261021);
        var rows = ImmutableArray.CreateBuilder<ScanFileOccurrence>(Photos);
        var vectors = new List<StoredEmbedding>(Photos);
        for (var id = 1L; id <= Photos; id++)
        {
            var path = $@"C:\photos\{id.ToString(CultureInfo.InvariantCulture)}.jpg";
            var key = new PhysicalFileKey(7, new(0, (ulong)id));
            var occurrence = ScanIdentityData.Occurrence(identity.Pin, id, path, path, key);
            Assert.Equal(ScanIdentityPersistOutcome.Persisted,
                (await stores.ScanIdentities.PersistOccurrenceAsync(occurrence, Token)).Outcome);
            var sha256 = id.ToString("X64", CultureInfo.InvariantCulture);
            rows.Add(new(occurrence.Token, path, path, key, new(ScanStageState.Succeeded, ScanReason.None),
                new(ScanStageState.Succeeded, ScanReason.None), new(key, 1024, sha256), new(1024, 1, true)));
            vectors.Add(new(id, sha256, id == Target ? target : RandomUnit(random, profile.Dimension)));
        }
        await stores.Embeddings.AddAsync(identity.Pin.ScanId, profile, vectors, Token);
        var scan = new ScanSnapshot(identity.Pin, ScanStatus.Complete, new(ScanStageState.Succeeded, ScanReason.None),
            rows.MoveToImmutable(), [], new(Photos, Photos, 0, 0, 0, 0))
        {
            Semantic = new(SemanticState.Ready, Photos, Photos, Photos, profile.Fingerprint, ""),
        };
        var query = new LikelyMatchesQuery(encoder, stores.Embeddings);

        var first = await query.RankAsync(scan, Prompt, Token);
        var times = new List<double>();
        for (var run = 0; run < 5; run++)
        {
            var clock = Stopwatch.StartNew();
            var again = await query.RankAsync(scan, Prompt, Token);
            times.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(first.Matches, again.Matches);
        }

        Assert.Equal(LikelyMatchesOutcome.Matches, first.Outcome);
        Assert.Equal(Target, first.Matches[0].OccurrenceId);
        Assert.Equal(1f, first.Matches[0].Score, 3);
        times.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"likely-matches: photos={Photos} matches={first.Matches.Length} " +
            $"ms-per-prompt-median={times[times.Count / 2]:F1} ms-per-prompt-max={times[^1]:F1}");
    }

    private static float[] RandomUnit(Random random, int dimension)
    {
        var vector = new float[dimension];
        var norm = 0.0;
        for (var i = 0; i < dimension; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
            norm += (double)vector[i] * vector[i];
        }
        var scale = (float)(1 / Math.Sqrt(norm));
        for (var i = 0; i < dimension; i++) vector[i] *= scale;
        return vector;
    }
}
