using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Analysis.NearDuplicate;
using Albumen.Application.Semantic;
using Albumen.Corpus;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Onnx;
using Xunit;

namespace Albumen.Infrastructure.Tests.Corpus;

public sealed class CorpusReportTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("albumen-corpus-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void PairsBlurAndSearchAreScoredAgainstTheLabels()
    {
        CorpusPhoto[] photos =
        [
            new("burst dog/1.jpg", "", "burst dog", CorpusLabels.Sharp),
            new("burst dog/blur-2.jpg", "", "burst dog", CorpusLabels.ObviousBlur),
            new("single/3.jpg", "", "", CorpusLabels.Sharp),
        ];
        CorpusFeatures[] features =
        [
            new(Signature(0), 100, [1, 0]),
            new(Signature(0), 10, [0.96f, 0.28f]),
            new(Signature(ulong.MaxValue), 50, [0, 1]),
        ];

        // With scale 100 and bias -10, "dog" lists only the first photo: the blurred one is more than
        // ln 50 / 100 below it. "a giraffe" has no answer; its best photo scores 0.05%, so a 0.1%
        // minimum score lists nothing.
        SearchAnswer[] answers = [new("dog", ["burst dog/1.jpg", "burst dog/blur-2.jpg"]), new("a giraffe", [])];
        float[][] prompts = [[1, 0], [0.024f, -0.9997f]];

        var report = CorpusReport.Write("v", "code", Profile, photos, features, "k", answers, prompts);

        Assert.Contains("\n| Look-alike (hash Near) | 1 | 1 | 100.0% | 100.0% | 100.0% | – | – | – | – |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| Maybe similar (hash Uncertain) | 0 | 0 | – | 0.0% | 0.0% | – | – | – | – |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 0.95 | 0 | 0 | – | 0.0% | 0.0% | – | – | – | – | 100.0% |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| burst dog/blur-2.jpg | single/3.jpg | 0.280 | NotRelated |\n", report, StringComparison.Ordinal);
        Assert.Contains("obvious blur; target 90%): 100.0%.", report, StringComparison.Ordinal);
        Assert.Contains("\"sharpest\" photo: 1 of 1; that photo is labelled sharp in 1.", report, StringComparison.Ordinal);
        Assert.Contains("\n| burst dog/blur-2.jpg | obvious-blur | 10.0 | 10.0× | yes |\n", report, StringComparison.Ordinal);
        Assert.Contains("Answer key `k`", report, StringComparison.Ordinal);
        Assert.Contains("\n| dog | 2 | 1 | 1 | 50.0% | 100.0% | 100% |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| a giraffe | 0 | 1 | 0 | – | 0.0% | 0.05% |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 0.01% (shipped) | 50.0% | 100.0% | 0 | 0 | 1 |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 0.1% | 50.0% | 100.0% | 0 | 1 | 0 |\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchPrecisionCountsTheFirstTenListedAndTheSweepCountsPromptsLeftEmpty()
    {
        // Twelve photos 0.001 apart for "boat", all within ln 50 / 100 of the best; the two expected
        // photos rank 11th and 12th. "kite" finds only photo 0, at 0.05%, which a 0.1% minimum hides.
        var photos = Enumerable.Range(0, 12).Select(index => new CorpusPhoto($"single/{index:00}.jpg", "", "", CorpusLabels.Sharp)).ToArray();
        var features = Enumerable.Range(0, 12)
            .Select(index => new CorpusFeatures(Signature(0), null, [0.99f - 0.001f * index, index == 0 ? 0.024f : 0, 0])).ToArray();
        SearchAnswer[] answers = [new("boat", ["single/10.jpg", "single/11.jpg"]), new("kite", ["single/00.jpg"])];
        float[][] prompts = [[1, 0, 0], [0, 1, 0]];

        var report = CorpusReport.Write("v", "code", Profile, photos, features, "k", answers, prompts);

        Assert.Contains("\n| boat | 2 | 12 | 2 | 100.0% | 0.0% | 100% |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| kite | 1 | 1 | 1 | 100.0% | 100.0% | 0.05% |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 0.01% (shipped) | 100.0% | 23.1% | 0 | 0 | 0 |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 0.1% | 66.7% | 16.7% | 1 | 0 | 0 |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 10% | 66.7% | 16.7% | 1 | 0 | 0 |\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameCorpusAndCodeGiveTheSameReport()
    {
        Draw("burst bars/1.png", shift: 0, vertical: true);
        Draw("burst bars/2.png", shift: 6, vertical: true);
        Draw("single/3.png", shift: 0, vertical: false);
        CorpusLabels.Write(root, CorpusLabels.FromFolders(root).Photos);
        File.WriteAllText(Path.Combine(root, SearchAnswers.FileName), "stripes: burst bars/1.png; burst bars/2.png\na giraffe:\n");
        var models = Directory.CreateTempSubdirectory("albumen-models-").FullName;
        try
        {
            foreach (var name in new[] { "manifest.json", "vision.onnx", "text.onnx", "tokenizer.model" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Onnx", "Fixtures", name), Path.Combine(models, name));

            var first = await ReportAsync(models);
            var second = await ReportAsync(models);

            Assert.Equal(first, second);
            Assert.Contains("3 photos, 1 sets, 1 single photos.", first, StringComparison.Ordinal);
            Assert.Contains("0 photos are smaller than the 1,024-pixel measuring size", first, StringComparison.Ordinal);
            Assert.Contains("2 prompts, 1 with no photo in the corpus.", first, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(models, recursive: true);
        }
    }

    private async Task<string> ReportAsync(string models)
    {
        var photos = CorpusLabels.ReadVerified(root);
        var answers = SearchAnswers.Read(root, photos);
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;
        var features = new List<CorpusFeatures>();
        foreach (var photo in photos)
            features.Add(await CorpusMeasurement.MeasureAsync(Path.Combine(root, photo.Path), encoder, profile));
        var prompts = answers.Select(answer => CorpusMeasurement.EncodePrompt(answer.Prompt, encoder, profile)).ToList();
        return CorpusReport.Write(CorpusLabels.Version(root), "code", profile, photos, features,
            SearchAnswers.Version(root), answers, prompts);
    }

    // 1,200 × 800 stripes, so the photo reaches the 1,024-pixel sharpness decode.
    private void Draw(string path, int shift, bool vertical)
    {
        const int width = 1200, height = 800;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(((vertical ? x + shift : y) / 40 % 2 == 0) ? 230 : 30);
            pixels.AsSpan((y * width + x) * 4, 4).Fill(value);
        }

        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4)));
        using var stream = File.Create(full);
        encoder.Save(stream);
    }

    private static readonly EncoderProfile Profile = new(
        "model", new string('A', 64), new string('B', 64), new string('C', 64), 512, 224, 64, 0, 2, 1, 100, -10);

    private static PerceptualSignature Signature(ulong hash) =>
        new(ManagedPerceptualSignature.ImplementationRevision, hash, 128, 100, 100, 100);
}
