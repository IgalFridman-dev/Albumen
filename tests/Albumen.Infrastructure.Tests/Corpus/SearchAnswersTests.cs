using Albumen.Application.Semantic;
using Albumen.Corpus;
using Xunit;

namespace Albumen.Infrastructure.Tests.Corpus;

public sealed class SearchAnswersTests : IDisposable
{
    private static readonly CorpusPhoto[] Photos =
    [
        new("burst dog/1.jpg", new('A', 64), "burst dog", CorpusLabels.Sharp),
        new("burst dog/2.jpg", new('B', 64), "burst dog", CorpusLabels.Sharp),
        new("single/IMG_3.JPG", new('C', 64), "", CorpusLabels.Sharp),
    ];

    private readonly string root = Directory.CreateTempSubdirectory("albumen-corpus-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void AWellFormedKeyIsReadAgainstTheLabels()
    {
        // Paths as Explorer gives them: backslashes, any letter case, quotes from "Copy as path".
        Write("  a   dog : burst dog/1.jpg;\"burst dog\\2.jpg\" ;\n\na red bicycle: single/img_3.jpg\na giraffe:\na sign: stop:\n");

        Assert.Equal(
            ["a dog|burst dog/1.jpg,burst dog/2.jpg", "a red bicycle|single/IMG_3.JPG", "a giraffe|", "a sign: stop|"],
            SearchAnswers.Read(root, Photos).Select(answer => $"{answer.Prompt}|{string.Join(',', answer.Photos)}"));
    }

    [Fact]
    public void PhotosMayFollowThePromptOnePerLineBetweenSeparators()
    {
        Write("a dog:\nburst dog/1.jpg\n burst dog/2.jpg \n\n-----\n\na red bicycle: single/img_3.jpg\n\n---\n\na giraffe:\n");

        Assert.Equal(
            ["a dog|burst dog/1.jpg,burst dog/2.jpg", "a red bicycle|single/IMG_3.JPG", "a giraffe|"],
            SearchAnswers.Read(root, Photos).Select(answer => $"{answer.Prompt}|{string.Join(',', answer.Photos)}"));
    }

    [Theory]
    [InlineData("a dog burst dog/1.jpg")]
    [InlineData(": burst dog/1.jpg")]
    [InlineData("LONG:")]
    [InlineData("a dog: burst dog/9.jpg")]
    [InlineData("a dog: burst dog/1.jpg; burst dog/1.jpg")]
    [InlineData("a dog: burst dog/1.jpg\nburst dog/1.jpg")]
    [InlineData("---\n")]
    [InlineData("a dog: burst dog/1.jpg\nA  Dog:")]
    [InlineData("\n")]
    public void KeysTheReportCannotScoreAreRejected(string key)
    {
        Write(key.Replace("LONG", new string('a', LikelyMatchesQuery.MaximumPromptLength + 1), StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => SearchAnswers.Read(root, Photos));
    }

    [Fact]
    public void AMissingKeyIsReported() =>
        Assert.Contains(SearchAnswers.FileName,
            Assert.Throws<InvalidDataException>(() => SearchAnswers.Read(root, Photos)).Message, StringComparison.Ordinal);

    [Fact]
    public void TheVersionFollowsTheKey()
    {
        Write("a dog: burst dog/1.jpg\n");
        var version = SearchAnswers.Version(root);

        Write("a dog: burst dog/1.jpg; burst dog/2.jpg\n");

        Assert.Equal(12, version.Length);
        Assert.NotEqual(version, SearchAnswers.Version(root));
    }

    private void Write(string key) => File.WriteAllText(Path.Combine(root, SearchAnswers.FileName), key);
}
