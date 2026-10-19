using Albumen.Application.Semantic;
using Xunit;

namespace Albumen.Application.Tests.Semantic;

public sealed class EncoderProfileTests
{
    private static readonly EncoderProfile Profile = new(
        "google/siglip2-base-patch16-224",
        new string('A', 64),
        new string('B', 64),
        new string('C', 64),
        DecodeEdge: 512,
        ImageSize: 224,
        MaxTokens: 64,
        PadId: 0,
        Dimension: 768,
        PipelineRevision: 1);

    [Fact]
    public void FingerprintIsTheSha256OfAFixedCanonicalForm()
    {
        Assert.Equal(
            "schema=1\nmodel=google/siglip2-base-patch16-224\n" +
            $"vision.sha256={new string('A', 64)}\ntext.sha256={new string('B', 64)}\ntokenizer.sha256={new string('C', 64)}\n" +
            "decode.edge=512\nimage.size=224\ntokens.max=64\ntokens.pad=0\ndimension=768\npipeline=1",
            Profile.CanonicalForm);
        Assert.Equal("820fa1e42881a06776a748bbd6a5dd49b148179a4c60bb9fca24666f448fbe28", Profile.Fingerprint);
    }

    [Fact]
    public void EveryFieldChangeGivesADistinctFingerprint()
    {
        EncoderProfile[] mutations =
        [
            Profile with { Model = "google/siglip2-base-patch32-256" },
            Profile with { VisionSha256 = new string('D', 64) },
            Profile with { TextSha256 = new string('D', 64) },
            Profile with { TokenizerSha256 = new string('D', 64) },
            Profile with { DecodeEdge = 256 },
            Profile with { ImageSize = 256 },
            Profile with { MaxTokens = 63 },
            Profile with { PadId = 1 },
            Profile with { Dimension = 512 },
            Profile with { PipelineRevision = 2 },
        ];

        var fingerprints = mutations.Select(p => p.Fingerprint).Append(Profile.Fingerprint).ToList();

        Assert.Equal(fingerprints.Count, fingerprints.Distinct(StringComparer.Ordinal).Count());
        Assert.All(mutations, mutation => Assert.NotEqual(Profile, mutation));
    }
}
