using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Analysis.NearDuplicate;
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
    public void PairsAndBlurAreScoredAgainstTheLabels()
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

        var report = CorpusReport.Write("v", "code", "model", photos, features);

        Assert.Contains("\n| Look-alike (hash Near) | 1 | 1 | 100.0% | 100.0% | 100.0% | – | – | – | – |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| Maybe similar (hash Uncertain) | 0 | 0 | – | 0.0% | 0.0% | – | – | – | – |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| 0.95 | 0 | 0 | – | 0.0% | 0.0% | – | – | – | – | 100.0% |\n", report, StringComparison.Ordinal);
        Assert.Contains("\n| burst dog/blur-2.jpg | single/3.jpg | 0.280 | NotRelated |\n", report, StringComparison.Ordinal);
        Assert.Contains("obvious blur; target 90%): 100.0%.", report, StringComparison.Ordinal);
        Assert.Contains("\"sharpest\" photo: 1 of 1; that photo is labelled sharp in 1.", report, StringComparison.Ordinal);
        Assert.Contains("\n| burst dog/blur-2.jpg | obvious-blur | 10.0 | 10.0× | yes |\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameCorpusAndCodeGiveTheSameReport()
    {
        Draw("burst bars/1.png", shift: 0, vertical: true);
        Draw("burst bars/2.png", shift: 6, vertical: true);
        Draw("single/3.png", shift: 0, vertical: false);
        CorpusLabels.Write(root, CorpusLabels.FromFolders(root).Photos);
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
        }
        finally
        {
            Directory.Delete(models, recursive: true);
        }
    }

    private async Task<string> ReportAsync(string models)
    {
        var photos = CorpusLabels.ReadVerified(root);
        using var encoder = new OnnxVisionLanguageEncoder(models);
        var profile = encoder.LoadProfile().Profile!;
        var features = new List<CorpusFeatures>();
        foreach (var photo in photos)
            features.Add(await CorpusMeasurement.MeasureAsync(Path.Combine(root, photo.Path), encoder, profile));
        return CorpusReport.Write(CorpusLabels.Version(root), "code", profile.Model, photos, features);
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

    private static PerceptualSignature Signature(ulong hash) =>
        new(ManagedPerceptualSignature.ImplementationRevision, hash, 128, 100, 100, 100);
}
