using System.Security.Cryptography;
using System.Text;
using Albumen.Corpus;
using Xunit;

namespace Albumen.Infrastructure.Tests.Corpus;

public sealed class CorpusLabelsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("albumen-corpus-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void FoldersAndFileNamePrefixesBecomeTheLabels()
    {
        Add("burst dog/blur-1.jpg", "a");
        Add("burst dog/2.JPG", "b");
        Add("single/soft-3.png", "c");
        Add("single/flat-4.png", "d");
        Add("single/notes.txt", "e");

        var (photos, ignored) = CorpusLabels.FromFolders(root);

        Assert.Equal(
            ["burst dog/2.JPG|burst dog|burst|sharp", "burst dog/blur-1.jpg|burst dog|burst|obvious-blur",
             "single/flat-4.png|||not-applicable", "single/soft-3.png|||ambiguous"],
            photos.Select(photo => $"{photo.Path}|{photo.Set}|{photo.Kind}|{photo.Blur}"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("a"))), photos[1].Sha256);
        Assert.Equal(["single/notes.txt"], ignored);
    }

    [Fact]
    public void LabelsReadBackOnlyWhileThePhotosAreUnchangedAndTheVersionFollowsThem()
    {
        Add("exposure sunset/1.jpg", "a");
        Add("exposure sunset/2.jpg", "b");
        Add("single/3.jpg", "c");
        var photos = CorpusLabels.FromFolders(root).Photos;
        CorpusLabels.Write(root, photos);
        var version = CorpusLabels.Version(root);
        File.WriteAllText(Path.Combine(root, $"report-{version}.md"), "earlier report");

        Assert.Equal(photos, CorpusLabels.ReadVerified(root));
        CorpusLabels.Write(root, CorpusLabels.FromFolders(root).Photos);
        Assert.Equal(version, CorpusLabels.Version(root));

        File.Move(Path.Combine(root, "single", "3.jpg"), Path.Combine(root, "single", "blur-3.jpg"));
        Assert.Contains("single/blur-3.jpg", Assert.Throws<InvalidDataException>(() => CorpusLabels.ReadVerified(root)).Message, StringComparison.Ordinal);
        CorpusLabels.Write(root, CorpusLabels.FromFolders(root).Photos);
        Assert.NotEqual(version, CorpusLabels.Version(root));

        Add("exposure sunset/1.jpg", "changed");
        Assert.Contains("exposure sunset/1.jpg", Assert.Throws<InvalidDataException>(() => CorpusLabels.ReadVerified(root)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("holiday/1.jpg", "holiday/2.jpg")]
    [InlineData("burst dog/1.jpg", "single/2.jpg")]
    [InlineData("burst dog/1.jpg", "burst dog/a,b.jpg")]
    [InlineData("burst dog/1.jpg", "burst dog/inner/2.jpg")]
    public void LayoutsTheLabelsCannotHoldAreRejected(string first, string second)
    {
        Add(first, "a");
        Add(second, "b");

        Assert.Throws<InvalidDataException>(() => CorpusLabels.FromFolders(root));
    }

    [Fact]
    public void WellFormedLabelsAreRead()
    {
        File.WriteAllText(Path.Combine(root, CorpusLabels.FileName),
            $"{CorpusLabels.Header}\nburst dog/1.jpg,{Sha},burst dog,sharp\nburst dog/2.jpg,{Sha},burst dog,obvious-blur\nsingle/3.png,{Sha},,not-applicable\n");

        Assert.Equal(["burst dog/1.jpg", "burst dog/2.jpg", "single/3.png"], CorpusLabels.Read(root).Select(photo => photo.Path));
    }

    [Theory]
    [InlineData("path,sha256,set", "single/1.jpg,SHA,,sharp")]
    [InlineData(CorpusLabels.Header, "single/1.jpg,SHA,sharp")]
    [InlineData(CorpusLabels.Header, "single/1.gif,SHA,,sharp")]
    [InlineData(CorpusLabels.Header, "1.jpg,SHA,,sharp")]
    [InlineData(CorpusLabels.Header, "single/1.jpg,LOW,,sharp")]
    [InlineData(CorpusLabels.Header, "single/1.jpg,SHA,,blurry")]
    [InlineData(CorpusLabels.Header, "single/1.jpg,SHA,burst dog,sharp")]
    [InlineData(CorpusLabels.Header, "burst dog/1.jpg,SHA,burst dog,sharp")]
    [InlineData(CorpusLabels.Header, "holiday/1.jpg,SHA,holiday,sharp\nholiday/2.jpg,SHA,holiday,sharp")]
    [InlineData(CorpusLabels.Header, "single/1.jpg,SHA,,sharp\nsingle/1.jpg,SHA,,sharp")]
    public void MalformedLabelsAreRejected(string header, string lines)
    {
        File.WriteAllText(Path.Combine(root, CorpusLabels.FileName),
            $"{header}\n{lines.Replace("SHA", Sha, StringComparison.Ordinal).Replace("LOW", new string('a', 64), StringComparison.Ordinal)}\n");

        Assert.Throws<InvalidDataException>(() => CorpusLabels.Read(root));
    }

    private static string Sha => new('A', 64);

    private void Add(string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
