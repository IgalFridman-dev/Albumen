using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Albumen.Infrastructure.Tests.Exact;

internal sealed class ExactCorpus : IAsyncDisposable
{
    private const string ManifestFileName = "ExactCorpusManifest.json";
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string container;

    private ExactCorpus(
        string container,
        string root,
        ImmutableArray<ImmutableArray<string>> expectedPhysicalGroups,
        ImmutableDictionary<string, string> aliasTargets)
    {
        this.container = container;
        Root = root;
        ExpectedPhysicalGroups = expectedPhysicalGroups;
        AliasTargets = aliasTargets;
    }

    public string Root { get; }

    public ImmutableArray<ImmutableArray<string>> ExpectedPhysicalGroups { get; }

    public ImmutableDictionary<string, string> AliasTargets { get; }

    public static async Task<ExactCorpus> CreateAsync(string root, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var parent = Path.GetFullPath(root);
        Directory.CreateDirectory(parent);
        var container = Path.Combine(parent, $"AlbumenExactCorpus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(container);

        try
        {
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "Exact", ManifestFileName);
            await using var manifestStream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<CorpusManifest>(
                manifestStream,
                ManifestJson,
                token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The exact corpus manifest is empty.");
            Validate(manifest);

            foreach (var file in manifest.Files.Where(file => file.Recipe is not null))
            {
                token.ThrowIfCancellationRequested();
                var path = Resolve(container, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, Recipe(file.Recipe!), token).ConfigureAwait(false);
            }

            foreach (var file in manifest.Files.Where(file => file.CopyOf is not null))
            {
                token.ThrowIfCancellationRequested();
                var path = Resolve(container, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Resolve(container, file.CopyOf!), path);
            }

            foreach (var file in manifest.Files.Where(file => file.HardLinkTo is not null))
            {
                token.ThrowIfCancellationRequested();
                var path = Resolve(container, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!CreateHardLink(Extended(path), Extended(Resolve(container, file.HardLinkTo!)), 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            var scopeRoot = Resolve(container, manifest.ScopeRoot);
            var inScope = manifest.Files
                .Where(file => IsUnder(file.Path, manifest.ScopeRoot))
                .ToArray();
            var physicalLabels = manifest.Files.ToDictionary(
                file => file.Path,
                file => UltimateHardLinkTarget(file, manifest.Files),
                StringComparer.Ordinal);
            var canonicalByPhysical = inScope
                .GroupBy(file => physicalLabels[file.Path], StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => RelativeToScope(
                        group.Select(file => file.Path).Order(StringComparer.Ordinal).First(),
                        manifest.ScopeRoot),
                    StringComparer.Ordinal);
            var aliases = inScope
                .GroupBy(file => physicalLabels[file.Path], StringComparer.Ordinal)
                .SelectMany(group => group
                    .Select(file => RelativeToScope(file.Path, manifest.ScopeRoot))
                    .Order(StringComparer.Ordinal)
                    .Skip(1)
                    .Select(alias => new KeyValuePair<string, string>(alias, canonicalByPhysical[group.Key])))
                .ToImmutableDictionary(StringComparer.Ordinal);
            var expected = inScope
                .Where(file => file.Expectation == "supported" && file.Equivalence is not null)
                .GroupBy(file => file.Equivalence!, StringComparer.Ordinal)
                .Select(group => group
                    .Select(file => canonicalByPhysical[physicalLabels[file.Path]])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray())
                .Where(group => group.Length >= 2)
                .OrderBy(group => string.Join('\0', group), StringComparer.Ordinal)
                .ToImmutableArray();

            return new ExactCorpus(container, scopeRoot, expected, aliases);
        }
        catch
        {
            Directory.Delete(container, recursive: true);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        var parent = Path.GetDirectoryName(container)
            ?? throw new InvalidOperationException("The corpus container has no parent.");
        if (!Path.GetFileName(container).StartsWith("AlbumenExactCorpus-", StringComparison.Ordinal)
            || !IsContained(parent, container))
        {
            throw new InvalidOperationException("The corpus cleanup target is invalid.");
        }

        Directory.Delete(container, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void Validate(CorpusManifest manifest)
    {
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.ScopeRoot) || manifest.Files.Length == 0)
        {
            throw new InvalidDataException("Only non-empty exact corpus manifest version 1 is supported.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (!paths.Add(file.Path) || CountSources(file) != 1)
            {
                throw new InvalidDataException($"Invalid or duplicate corpus entry: {file.Path}");
            }
        }

        foreach (var file in manifest.Files)
        {
            var dependency = file.CopyOf ?? file.HardLinkTo;
            if (dependency is not null && !paths.Contains(dependency))
            {
                throw new InvalidDataException($"Unknown corpus dependency: {dependency}");
            }
        }
    }

    private static int CountSources(CorpusFile file) =>
        (file.Recipe is null ? 0 : 1) + (file.CopyOf is null ? 0 : 1) + (file.HardLinkTo is null ? 0 : 1);

    private static string UltimateHardLinkTarget(CorpusFile file, ImmutableArray<CorpusFile> files)
    {
        var current = file;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current.HardLinkTo is { } target)
        {
            if (!visited.Add(current.Path)) throw new InvalidDataException("Hard-link dependency cycle.");
            current = files.Single(candidate => candidate.Path == target);
        }

        return current.Path;
    }

    private static byte[] Recipe(string recipe) => recipe switch
    {
        "empty" => [],
        "abc" => "abc"u8.ToArray(),
        "sequence-19" => Sequence(19),
        "sequence-257" => Sequence(257),
        "sequence-333" => Sequence(333),
        "sequence-521" => Sequence(521),
        "sequence-4097" => Sequence(4097),
        "sequence-8193" => Sequence(8193),
        "sequence-131073" => Sequence(131073),
        "sequence-131073-flip-last" => FlipLast(Sequence(131073)),
        _ => throw new InvalidDataException($"Unknown exact corpus recipe: {recipe}"),
    };

    private static byte[] Sequence(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + 17) % 251);
        }

        return bytes;
    }

    private static byte[] FlipLast(byte[] bytes)
    {
        bytes[^1] ^= 0x01;
        return bytes;
    }

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
            throw new InvalidDataException("Corpus paths must be non-empty and relative.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(root, result)) throw new InvalidDataException("Corpus path escaped the fixture root.");
        return result;
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        var normalizedRoot = root.Replace('\\', '/').Trim('/');
        return normalizedPath.StartsWith(normalizedRoot + '/', StringComparison.Ordinal);
    }

    private static string RelativeToScope(string path, string scopeRoot)
    {
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        var normalizedRoot = scopeRoot.Replace('\\', '/').Trim('/');
        if (!normalizedPath.StartsWith(normalizedRoot + '/', StringComparison.Ordinal))
            throw new InvalidDataException("Corpus file is outside the scope root.");
        return normalizedPath[(normalizedRoot.Length + 1)..];
    }

    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path
        : @"\\?\" + path;

#pragma warning disable SYSLIB1054 // Test-only hard-link fixture import.
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
#pragma warning restore SYSLIB1054

    private sealed record CorpusManifest(int Version, string ScopeRoot, ImmutableArray<CorpusFile> Files);

    private sealed record CorpusFile(
        string Path,
        string? Recipe,
        string? CopyOf,
        string? HardLinkTo,
        string? Equivalence,
        string Expectation);
}
