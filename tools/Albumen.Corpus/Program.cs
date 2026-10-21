using System.Reflection;
using Albumen.Corpus;
using Albumen.Infrastructure.Onnx;

// E3-S6 corpus tool. The photos, labels.csv and the reports stay in the corpus folder, outside
// the repositories; see README.md for the layout.
const string Usage = """
    usage:
      Albumen.Corpus label  <corpus folder>                  write labels.csv from the folders
      Albumen.Corpus report <corpus folder> <models folder>  measure the photos, write report-<version>.md
    """;

try
{
    switch (args)
    {
        case ["label", var corpus]:
        {
            var (photos, ignored) = CorpusLabels.FromFolders(corpus);
            CorpusLabels.Write(corpus, photos);
            foreach (var file in ignored)
                Console.WriteLine($"Ignored (not JPEG or PNG): {file}");
            foreach (var photo in photos.Where(photo => photo.Set.Length == 0 && photo.Blur is CorpusLabels.ObviousBlur or CorpusLabels.Ambiguous))
                Console.WriteLine($"Not measured (blur is only compared within a set): {photo.Path}");
            foreach (var set in photos.Where(photo => photo.Set.Length > 0).GroupBy(photo => photo.Set)
                         .Where(set => set.Any(photo => photo.Blur == CorpusLabels.ObviousBlur) && set.All(photo => photo.Blur != CorpusLabels.Sharp)))
                Console.WriteLine($"Set without a sharp photo, so its blur is not measured: {set.Key}");
            Console.WriteLine($"Wrote {CorpusLabels.FileName} {CorpusLabels.Version(corpus)}: {photos.Count} photos, "
                + $"{photos.Where(photo => photo.Set.Length > 0).Select(photo => photo.Set).Distinct().Count()} sets, "
                + string.Join(", ", CorpusLabels.BlurSlices.Select(slice => $"{photos.Count(photo => photo.Blur == slice)} {slice}")) + ".");
            return 0;
        }
        case ["report", var corpus, var models]:
        {
            var photos = CorpusLabels.ReadVerified(corpus);
            using var encoder = new OnnxVisionLanguageEncoder(models);
            var loaded = encoder.LoadProfile();
            if (loaded.Profile is not { } profile)
                throw new InvalidDataException($"No usable model in {models}: {loaded.Failure?.Reason}");

            var features = new List<CorpusFeatures>(photos.Count);
            foreach (var photo in photos)
            {
                features.Add(await CorpusMeasurement.MeasureAsync(Path.Combine(corpus, photo.Path), encoder, profile).ConfigureAwait(false));
                if (features.Count % 25 == 0 || features.Count == photos.Count)
                    Console.WriteLine($"Measured {features.Count} of {photos.Count}");
            }

            var version = CorpusLabels.Version(corpus);
            var code = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var path = Path.Combine(corpus, $"report-{version}.md");
            File.WriteAllText(path, CorpusReport.Write(version, code, $"{profile.Model}, vision {profile.VisionSha256[..12]}", photos, features));
            Console.WriteLine($"Wrote {path}");
            return 0;
        }
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (InvalidDataException problem)
{
    Console.Error.WriteLine(problem.Message);
    return 1;
}
