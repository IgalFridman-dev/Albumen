using System.Security.Cryptography;
using System.Text;

namespace Albumen.Corpus;

// One labelled photo. Path is relative to the corpus folder with '/' separators; Set is the
// folder name, empty for the "single" folder.
internal sealed record CorpusPhoto(string Path, string Sha256, string Set, string Blur)
{
    public string? Kind => Set.Length == 0 ? null : CorpusLabels.KindOf(Set);
}

// The corpus layout is the labelling: one folder per set whose name starts with its kind, a
// "single" folder for photos that belong to no set, and a file-name prefix for the blur slice.
// labels.csv freezes that layout with each photo's SHA-256; its own hash is the corpus version.
internal static class CorpusLabels
{
    public const string FileName = "labels.csv";
    public const string SingleFolder = "single";
    public const string Header = "path,sha256,set,blur";

    public static readonly string[] Kinds = ["burst", "exposure", "edit", "scan", "screenshot"];

    public const string Sharp = "sharp";
    public const string ObviousBlur = "obvious-blur";
    public const string Ambiguous = "ambiguous";
    public const string NotApplicable = "not-applicable";
    public static readonly string[] BlurSlices = [Sharp, ObviousBlur, Ambiguous, NotApplicable];

    private static readonly (string Prefix, string Blur)[] BlurPrefixes =
        [("blur-", ObviousBlur), ("soft-", Ambiguous), ("flat-", NotApplicable)];

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    // Skips hidden and system files such as Thumbs.db and desktop.ini.
    private static readonly EnumerationOptions Visible = new();

    public static string? KindOf(string set) =>
        Kinds.FirstOrDefault(kind => kind.Equals(set.Split(' ')[0], StringComparison.OrdinalIgnoreCase));

    // Reads the folders; Ignored lists files that are not JPEG or PNG.
    public static (IReadOnlyList<CorpusPhoto> Photos, IReadOnlyList<string> Ignored) FromFolders(string root)
    {
        var photos = new List<CorpusPhoto>();
        var ignored = Directory.EnumerateFiles(root, "*", Visible)
            .Select(file => Path.GetFileName(file))
            .Where(name => name != FileName && !name.StartsWith("report-", StringComparison.Ordinal))
            .ToList();
        foreach (var folder in Directory.EnumerateDirectories(root, "*", Visible).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(folder);
            var set = name.Equals(SingleFolder, StringComparison.OrdinalIgnoreCase) ? "" : name;
            if (set.Length > 0 && KindOf(set) is null)
                throw new InvalidDataException(
                    $"Folder \"{name}\" must start with a kind ({string.Join(", ", Kinds)}) or be named \"{SingleFolder}\".");
            if (Directory.EnumerateDirectories(folder, "*", Visible).Any())
                throw new InvalidDataException($"Folder \"{name}\" has a subfolder; sets are one level deep.");

            var files = Directory.EnumerateFiles(folder, "*", Visible).Order(StringComparer.Ordinal).ToList();
            var images = files.Where(IsImage).ToList();
            ignored.AddRange(files.Except(images).Select(file => Relative(root, file)));
            if (set.Length > 0 && images.Count < 2)
                throw new InvalidDataException($"Set \"{name}\" needs at least two photos; move a lone photo to \"{SingleFolder}\".");
            photos.AddRange(images.Select(image =>
                new CorpusPhoto(Relative(root, image), Sha256(image), set, BlurOf(Path.GetFileName(image)))));
        }

        if (photos.Count == 0)
            throw new InvalidDataException($"No JPEG or PNG photos in the folders of {root}.");
        return (photos, ignored);
    }

    public static void Write(string root, IEnumerable<CorpusPhoto> photos)
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var photo in photos.OrderBy(photo => photo.Path, StringComparer.Ordinal))
            text.Append(photo.Path).Append(',').Append(photo.Sha256).Append(',')
                .Append(photo.Set).Append(',').Append(photo.Blur).Append('\n');
        File.WriteAllText(Path.Combine(root, FileName), text.ToString());
    }

    public static IReadOnlyList<CorpusPhoto> Read(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"No {FileName} in {root}; run the label command first.");
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2 || lines[0] != Header)
            throw new InvalidDataException($"{FileName} must start with the line \"{Header}\" and list at least one photo.");

        var photos = new List<CorpusPhoto>();
        for (var number = 2; number <= lines.Length; number++)
        {
            var fields = lines[number - 1].Split(',');
            string Problem(string what) => $"{FileName} line {number}: {what}";
            if (fields.Length != 4)
                throw new InvalidDataException(Problem("expected path,sha256,set,blur."));
            var photo = new CorpusPhoto(fields[0], fields[1], fields[2], fields[3]);
            var folder = photo.Path.Split('/')[0];
            if (photo.Path.Split('/').Length != 2 || !IsImage(photo.Path))
                throw new InvalidDataException(Problem("the path must be <folder>/<JPEG or PNG file>."));
            if (photo.Sha256.Length != 64 || !photo.Sha256.All(Uri.IsHexDigit) || photo.Sha256.Any(char.IsLower))
                throw new InvalidDataException(Problem("the SHA-256 must be 64 upper-case hex digits."));
            if (photo.Set.Length == 0 ? !folder.Equals(SingleFolder, StringComparison.OrdinalIgnoreCase)
                    : photo.Set != folder || photo.Kind is null)
                throw new InvalidDataException(Problem("the set must be the photo's folder, or empty for the single folder."));
            if (!BlurSlices.Contains(photo.Blur))
                throw new InvalidDataException(Problem($"the blur slice must be one of {string.Join(", ", BlurSlices)}."));
            if (photos.Any(other => other.Path == photo.Path))
                throw new InvalidDataException(Problem("the path is listed twice."));
            photos.Add(photo);
        }

        if (photos.Where(photo => photo.Set.Length > 0).GroupBy(photo => photo.Set).FirstOrDefault(set => set.Count() < 2) is { } lone)
            throw new InvalidDataException($"{FileName}: set \"{lone.Key}\" has only one photo.");
        return photos;
    }

    // labels.csv, checked against the folders as they are now.
    public static IReadOnlyList<CorpusPhoto> ReadVerified(string root)
    {
        var labelled = Read(root);
        var current = FromFolders(root).Photos;
        var changed = current.Except(labelled).Concat(labelled.Except(current))
            .Select(photo => photo.Path).Distinct().Order(StringComparer.Ordinal).ToList();
        if (changed.Count > 0)
            throw new InvalidDataException(
                $"The photos changed since {FileName} was written ({string.Join(", ", changed.Take(5))}); run the label command again.");
        return labelled;
    }

    public static string Version(string root) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, FileName))))[..12];

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string BlurOf(string fileName) =>
        BlurPrefixes.FirstOrDefault(entry => fileName.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase)).Blur
            ?? Sharp;

    private static string Relative(string root, string file)
    {
        var path = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (path.Contains(',', StringComparison.Ordinal))
            throw new InvalidDataException($"\"{path}\" has a comma; rename it, labels.csv cannot hold one.");
        return path;
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
