using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Analysis.Exact;
using Albumen.Analysis.NearDuplicate;
using Albumen.Analysis.Sharpness;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Preview;
using Xunit;

namespace Albumen.Infrastructure.Tests.Preview;

public sealed class WicNearFeatureSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BothFeaturesComeFromOneOpen()
    {
        var png = CreatePng(1200, 900);
        var reader = new MemoryReader(png);

        var result = await Source(reader).AnalyzeAsync(Context(), Occurrence(png, Digest(png)), NearFeatures.All, Token);

        Assert.Equal(ScanStageState.Succeeded, result.Outcome.State);
        Assert.Equal(BoundedNearDuplicatePipeline.ImplementationRevision, result.Signature!.ImplementationRevision);
        Assert.NotNull(result.Sharpness);
        Assert.Equal(1, reader.Opens);
    }

    [Fact]
    public async Task EachFeatureAloneIsTheOneBothWouldGive()
    {
        var png = CreatePng(1200, 900);
        var row = Occurrence(png, Digest(png));
        var both = await Source(new MemoryReader(png)).AnalyzeAsync(Context(), row, NearFeatures.All, Token);

        var signature = await Source(new MemoryReader(png)).AnalyzeAsync(Context(), row, NearFeatures.Signature, Token);
        var sharpness = await Source(new MemoryReader(png)).AnalyzeAsync(Context(), row, NearFeatures.Sharpness, Token);

        Assert.Equal(ScanStageState.Succeeded, signature.Outcome.State);
        Assert.Equal(both.Signature, signature.Signature);
        Assert.Null(signature.Sharpness);
        Assert.Equal(ScanStageState.Succeeded, sharpness.Outcome.State);
        Assert.Null(sharpness.Signature);
        Assert.Equal(both.Sharpness, sharpness.Sharpness);
    }

    [Fact]
    public async Task APhotoSmallerThanTheSharpnessFrameHasNoScore()
    {
        var png = CreatePng(40, 30);

        var result = await Source(new MemoryReader(png)).AnalyzeAsync(
            Context(), Occurrence(png, Digest(png)), NearFeatures.Sharpness, Token);

        Assert.Equal(ScanStageState.Succeeded, result.Outcome.State);
        Assert.Null(result.Sharpness);
    }

    [Fact]
    public async Task APhotoThatChangedSinceTheScanIsNotDecoded()
    {
        var png = CreatePng(40, 30);

        var result = await Source(new MemoryReader(png)).AnalyzeAsync(
            Context(), Occurrence(png, new string('0', 64)), NearFeatures.All, Token);

        Assert.Equal(new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed), result.Outcome);
        Assert.Null(result.Signature);
        Assert.Null(result.Sharpness);
    }

    [Fact]
    public async Task AskingForNothingIsAProgrammingError()
    {
        var png = CreatePng(40, 30);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Source(new MemoryReader(png)).AnalyzeAsync(
            Context(), Occurrence(png, Digest(png)), NearFeatures.None, Token).AsTask());
    }

    // The stored dHash and sharpness revisions describe these decodes: a change here needs a new
    // BoundedNearDuplicatePipeline.ImplementationRevision or SharpnessRevision.
    [Fact]
    public void TheDecodeEdgesTheStoredRevisionsDescribeAreUnchanged()
    {
        Assert.Equal(256, WicNearFeatureSource.MaximumEdge);
        Assert.Equal(1024, WicNearFeatureSource.SharpnessEdge);
        Assert.Equal("managed-dhash-bgra-v1", BoundedNearDuplicatePipeline.ImplementationRevision);
        Assert.Equal("managed-laplacian-bgra-1024-v1", BoundedNearDuplicatePipeline.SharpnessRevision);
    }

    private static WicNearFeatureSource Source(MemoryReader reader) => new(reader,
        new StreamingExactContentAnalyzer(), new WicPreviewDecoder(), new ManagedPerceptualSignature(), new ManagedSharpness());

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static ScanRunContext Context() => new(
        new(Guid.Parse("62000000-0000-0000-0000-000000000002"), 1, 1, 1),
        new(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"C:\Photos", new(7, 0, 70)));

    private static ScanFileOccurrence Occurrence(byte[] png, string digest)
    {
        var key = new PhysicalFileKey(7, new(0, 1));
        return new(new(Context().Pin, 1, 1, 1), @"C:\Photos\1.png", @"C:\Photos\1.png", key,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None),
            new(key, png.LongLength, digest), new(png.LongLength, 1, true));
    }

    private static byte[] CreatePng(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7 + i / (width * 4));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class MemoryReader(byte[] content) : IProtectedFileReader
    {
        public int Opens { get; private set; }

        public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken)
        {
            Opens++;
            return ValueTask.FromResult<IProtectedFileLease>(new MemoryLease(new MemoryStream(content, writable: false),
                new(request.ExpectedPhysicalFileKey, content.LongLength, request.ResolvedPath, DateTimeOffset.UnixEpoch,
                    new(content.LongLength, 1, true))));
        }
    }

    private sealed class MemoryLease(Stream stream, ProtectedFileFacts facts) : IProtectedFileLease
    {
        public Stream Content { get; } = stream;
        public ProtectedFileFacts OpenedFacts { get; } = facts;
        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken) => ValueTask.FromResult(OpenedFacts);
        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }
}
