using System.Security.Cryptography;
using Albumen.Application.Semantic;

namespace Albumen.Corpus;

// One prompt of the search answer key and the photos (labels.csv paths) the owner expects for it;
// none when no photo in the corpus fits.
internal sealed record SearchAnswer(string Prompt, IReadOnlyList<string> Photos);

// search-answers.txt, written by the owner next to labels.csv: one prompt per line, a colon, then
// the expected photos separated by ';'. Its own hash is the answer key's version.
internal static class SearchAnswers
{
    public const string FileName = "search-answers.txt";

    // The key read against the labelled photos: a renamed, removed or unknown photo stops it.
    public static IReadOnlyList<SearchAnswer> Read(string root, IReadOnlyList<CorpusPhoto> photos)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"No {FileName} in {root}; write the search answer key (see the README) and run the label command again.");
        // Paths come from Explorer: any letter case and '\' separators.
        var labelled = photos.ToDictionary(photo => photo.Path, photo => photo.Path, StringComparer.OrdinalIgnoreCase);

        var answers = new List<SearchAnswer>();
        var lines = File.ReadAllLines(path);
        for (var number = 1; number <= lines.Length; number++)
        {
            var line = lines[number - 1];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string Problem(string what) => $"{FileName} line {number}: {what}";
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
                throw new InvalidDataException(Problem("write the prompt, a colon, then its photos separated by ;."));
            var prompt = LikelyMatchesQuery.Normalize(line[..colon]);
            if (prompt.Length == 0 || prompt.Length > LikelyMatchesQuery.MaximumPromptLength)
                throw new InvalidDataException(Problem($"the prompt must have 1 to {LikelyMatchesQuery.MaximumPromptLength} characters, as in the app."));
            // The encoder lower-cases prompts, so "Dog" and "dog" are one prompt.
            if (answers.Any(answer => answer.Prompt.Equals(prompt, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(Problem($"\"{prompt}\" is listed twice."));

            var expected = new List<string>();
            foreach (var name in line[(colon + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!labelled.TryGetValue(name.Replace('\\', '/'), out var photo))
                    throw new InvalidDataException(Problem($"\"{name}\" is not a photo in {CorpusLabels.FileName}."));
                if (expected.Contains(photo))
                    throw new InvalidDataException(Problem($"\"{name}\" is listed twice."));
                expected.Add(photo);
            }
            answers.Add(new(prompt, expected));
        }

        return answers.Count > 0 ? answers : throw new InvalidDataException($"{FileName} lists no prompt.");
    }

    public static string Version(string root) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, FileName))))[..12];
}
