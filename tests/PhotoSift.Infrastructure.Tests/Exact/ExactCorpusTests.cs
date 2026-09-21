using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using PhotoSift.Analysis.Exact;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Sqlite;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Exact;

public sealed class ExactCorpusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string[] AliasOnlyPaths =
        ["alias-only/a-first-link.png", "alias-only/b-second-link.jpg"];
    private static readonly string[] CollisionGroupPaths =
        ["large/a-original.jpg", "large/b-renamed-copy.jpeg"];
    private static readonly ImmutableArray<(string Transformation, bool ExpectedExact)>
        NearQualityExactExpectations =
        [
            ("byte-identical", true),
            ("brightness", false),
            ("burst shift", false),
            ("recompression", false),
            ("resize ambiguity", false),
            ("crop", false),
            ("mirror", false),
            ("rotation", false),
            ("unrelated", false),
            ("decoder failure", false),
        ];

    [Fact]
    public async Task VersionOneCorpusPublishesEveryExpectedPhysicalGroupAndNoFalsePositive()
    {
        await using var corpus = await ExactCorpus.CreateAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken);
        var sourceTruth = Directory.EnumerateFiles(corpus.Root, "*", SearchOption.AllDirectories)
            .Where(IsSupportedSource)
            .ToDictionary(
                path => Relative(corpus, path),
                ReadSourceTruth,
                StringComparer.Ordinal);
        var reader = new ObservedProtectedFileReader(new WindowsProtectedFileReader());
        var executor = new BoundedScanExecutor(
            new WindowsScanEnumerator(), reader, new StreamingExactContentAnalyzer());
        await using var scan = await ExactScanFixture.CreateAsync(corpus.Root, executor);

        await scan.StartAsync();
        var snapshot = await scan.WaitForTerminalAsync();
        await scan.SettleAsync();

        Assert.Equal(ScanStatus.Complete, snapshot.Status);
        Assert.Equal(new StageOutcome(ScanStageState.Succeeded, ScanReason.None), snapshot.Enumeration);
        Assert.Equal(0, snapshot.Counts.Outstanding);
        Assert.Equal(0, reader.Live);
        Assert.Equal(sourceTruth.Count, snapshot.Occurrences.Count(occurrence =>
            IsSupportedSource(occurrence.DiscoveredPath)));
        foreach (var source in sourceTruth.OrderBy(source => source.Key, StringComparer.Ordinal))
        {
            var occurrence = Occurrence(snapshot, source.Key, corpus);
            Assert.Equal(source.Value.PhysicalFileKey, occurrence.PhysicalFileKey);
            Assert.Equal<uint?>(source.Value.LinkCount, occurrence.Allocation.LinkCount);
            Assert.True(occurrence.Allocation.Stable);
            Assert.Equal(
                new StageOutcome(ScanStageState.Succeeded, ScanReason.None),
                occurrence.Exact);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"E5-S2 source: path={source.Key}; key={source.Value.PhysicalFileKey}; " +
                $"links={source.Value.LinkCount}; outcome={occurrence.Exact}; " +
                $"sha256={source.Value.Sha256}");
            Assert.Equal(source.Value, ReadSourceTruth(PathFor(corpus, source.Key)));
        }

        var actual = snapshot.Groups
            .Select(group => group.Members
                .Select(member => Canonical(corpus, Relative(corpus, member.DiscoveredPath)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray())
            .OrderBy(group => string.Join('\0', group), StringComparer.Ordinal)
            .ToArray();
        var expected = corpus.ExpectedPhysicalGroups.Select(group => group.ToArray()).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }

        Assert.NotEmpty(snapshot.Groups);
        Assert.All(snapshot.Groups, group =>
        {
            Assert.Equal(CandidateCategory.Exact, group.Category);
            Assert.False(group.Uncertain);
            Assert.Null(group.KeeperOccurrenceId);
            Assert.True(group.Members.Select(member => member.PhysicalFileKey).Distinct().Count() >= 2);
        });

        foreach (var group in snapshot.Groups)
        {
            for (var left = 0; left < group.Members.Length; left++)
            {
                for (var right = left + 1; right < group.Members.Length; right++)
                {
                    if (group.Members[left].PhysicalFileKey == group.Members[right].PhysicalFileKey) continue;
                    var leftBytes = await File.ReadAllBytesAsync(
                        group.Members[left].ResolvedPath, TestContext.Current.CancellationToken);
                    var rightBytes = await File.ReadAllBytesAsync(
                        group.Members[right].ResolvedPath, TestContext.Current.CancellationToken);
                    Assert.True(leftBytes.AsSpan().SequenceEqual(rightBytes),
                        $"Reported cross-physical members differ: {group.Members[left].DiscoveredPath} and {group.Members[right].DiscoveredPath}");
                }
            }
        }

        foreach (var alias in corpus.AliasTargets)
        {
            var occurrence = Occurrence(snapshot, alias.Key, corpus);
            var target = Occurrence(snapshot, alias.Value, corpus);
            Assert.Equal(target.PhysicalFileKey, occurrence.PhysicalFileKey);
        }

        var large = GroupContaining(snapshot, "large/a-original.jpg", corpus);
        Assert.Equal(3, large.Members.Length);
        var largePhysicalCopies = large.Members
            .GroupBy(member => member.PhysicalFileKey)
            .ToArray();
        Assert.Equal(2, largePhysicalCopies.Length);
        Assert.Equal(RecoveryUnavailableReason.None, large.PotentialRecovery.Reason);
        var oneCopyAllocation = Assert.Single(largePhysicalCopies
            .Select(copy => Assert.Single(copy
                .Select(member => member.Allocation.AllocatedBytes)
                .Distinct()))
            .Distinct());
        Assert.True(oneCopyAllocation is > 0);
        Assert.Equal(oneCopyAllocation, large.PotentialRecovery.Bytes);

        var external = GroupContaining(snapshot, "external/a-external-link.jpg", corpus);
        Assert.Equal(RecoveryUnavailableReason.ExternalLinks, external.PotentialRecovery.Reason);
        Assert.Null(external.PotentialRecovery.Bytes);

        var aliasOnly = AliasOnlyPaths
            .Select(path => Occurrence(snapshot, path, corpus))
            .ToArray();
        Assert.Single(aliasOnly.Select(row => row.PhysicalFileKey).Distinct());
        Assert.DoesNotContain(snapshot.Groups, group =>
            group.Members.Any(member => aliasOnly.Select(row => row.Token).Contains(member.Token)));
        Assert.DoesNotContain(snapshot.Groups, group => group.Members.Any(member =>
            Relative(corpus, member.DiscoveredPath) == "large/d-equal-length-different.png"));
        var originalBytes = await File.ReadAllBytesAsync(
            PathFor(corpus, "large/a-original.jpg"), TestContext.Current.CancellationToken);
        var differentBytes = await File.ReadAllBytesAsync(
            PathFor(corpus, "large/d-equal-length-different.png"), TestContext.Current.CancellationToken);
        Assert.Equal(originalBytes.Length, differentBytes.Length);
        Assert.False(originalBytes.AsSpan().SequenceEqual(differentBytes));

        var unsupported = Occurrence(snapshot, "unsupported/abc.gif", corpus);
        Assert.Equal(new StageOutcome(
            ScanStageState.NotApplicable, ScanReason.UnsupportedExtension), unsupported.Exact);
        var heic = Occurrence(snapshot, "codec/conditional.heic", corpus);
        Assert.True(heic.Exact == new StageOutcome(ScanStageState.Succeeded, ScanReason.None)
            || heic.Exact == new StageOutcome(ScanStageState.Unsupported, ScanReason.CodecUnavailable));
        Assert.Equal(ScanStageState.Succeeded, Occurrence(snapshot, "תמונות-日本語/רחוב-唯一.png", corpus).Exact.State);
        Assert.Equal(ScanStageState.Succeeded, Occurrence(snapshot,
            "deep/segment-0-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/segment-1-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/segment-2-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/segment-3-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx/deep-unique.jpeg",
            corpus).Exact.State);
    }

    [Fact]
    public async Task NearQualityPairsProduceNoFalseExactGrouping()
    {
        var analyzer = new StreamingExactContentAnalyzer();

        foreach (var expected in NearQualityExactExpectations)
        {
            var pair = NearQualityExactCorpusRecipes.CreatePair(expected.Transformation);
            using var leftForHash = new MemoryStream(pair.Left, writable: false);
            using var rightForHash = new MemoryStream(pair.Right, writable: false);
            var leftDigest = await analyzer.HashAsync(leftForHash, pair.Left.LongLength,
                TestContext.Current.CancellationToken);
            var rightDigest = await analyzer.HashAsync(rightForHash, pair.Right.LongLength,
                TestContext.Current.CancellationToken);
            var groupedAsExact = false;
            if (pair.Left.LongLength == pair.Right.LongLength && leftDigest == rightDigest)
            {
                using var leftForComparison = new MemoryStream(pair.Left, writable: false);
                using var rightForComparison = new MemoryStream(pair.Right, writable: false);
                groupedAsExact = (await analyzer.CompareAsync(
                    leftForComparison,
                    rightForComparison,
                    pair.Left.LongLength,
                    TestContext.Current.CancellationToken)).Equal;
            }

            if (expected.ExpectedExact)
                Assert.True(groupedAsExact, $"{expected.Transformation} did not remain Exact.");
            else
                Assert.False(groupedAsExact, $"{expected.Transformation} was falsely grouped as Exact.");
        }
    }

    [Fact]
    public async Task EqualDigestLabelsWithDifferentBytesStillPartitionByStreamingEquality()
    {
        await using var corpus = await ExactCorpus.CreateAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken);
        var paths = new[]
        {
            "large/a-original.jpg",
            "large/b-renamed-copy.jpeg",
            "large/d-equal-length-different.png",
        };
        var reader = new ObservedProtectedFileReader(new WindowsProtectedFileReader());
        var executor = new BoundedScanExecutor(
            new OrderedSubsetEnumerator(corpus.Root, paths),
            reader,
            new CollisionLabelAnalyzer(new StreamingExactContentAnalyzer()));
        await using var scan = await ExactScanFixture.CreateAsync(corpus.Root, executor);

        await scan.StartAsync();
        var snapshot = await scan.WaitForTerminalAsync();
        await scan.SettleAsync();

        Assert.Equal(ScanStatus.Complete, snapshot.Status);
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(
            CollisionGroupPaths,
            group.Members.Select(member => Relative(corpus, member.DiscoveredPath)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(group.Members, member =>
            Relative(corpus, member.DiscoveredPath) == "large/d-equal-length-different.png");
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task ChangeAfterSignatureBeforeComparisonReopenIsRejectedAndClosesEveryLease()
    {
        await using var corpus = await ExactCorpus.CreateAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken);
        var beforeThirdOpen = Signal();
        var releaseThirdOpen = Signal();
        var reader = new ObservedProtectedFileReader(new WindowsProtectedFileReader())
        {
            BeforeOpenAsync = async (open, _, token) =>
            {
                if (open != 3) return;
                beforeThirdOpen.TrySetResult();
                await releaseThirdOpen.Task.WaitAsync(Timeout, token);
            },
        };
        var paths = new[] { "race/a-source.jpg", "race/b-copy.png" };
        var executor = new BoundedScanExecutor(
            new OrderedSubsetEnumerator(corpus.Root, paths), reader, new StreamingExactContentAnalyzer());
        await using var scan = await ExactScanFixture.CreateAsync(corpus.Root, executor);

        try
        {
            await scan.StartAsync();
            await Bound(beforeThirdOpen.Task);
            Assert.Equal(0, reader.Live);
            var changed = PathFor(corpus, "race/b-copy.png");
            var bytes = await File.ReadAllBytesAsync(changed, TestContext.Current.CancellationToken);
            bytes[0] ^= 0x01;
            await File.WriteAllBytesAsync(changed, bytes, TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseThirdOpen.TrySetResult();
        }

        var snapshot = await scan.WaitForTerminalAsync();
        await scan.SettleAsync();
        Assert.Equal(ScanStatus.Incomplete, snapshot.Status);
        Assert.Empty(snapshot.Groups);
        Assert.Equal(
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed),
            Occurrence(snapshot, "race/b-copy.png", corpus).Exact);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task FinalRefreshToOwnerAcknowledgmentRetainsLeaseAndRejectsStaleReplacement()
    {
        await using var corpus = await ExactCorpus.CreateAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken);
        var reader = new ObservedProtectedFileReader(new WindowsProtectedFileReader());
        var acknowledgmentEntered = Signal();
        var releaseAcknowledgment = Signal();
        var staleResult = new TaskCompletionSource<SignatureCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new InterceptingExecutor(
            new BoundedScanExecutor(
                new OrderedSubsetEnumerator(corpus.Root, ["unique/abc.jpg"]),
                reader,
                new StreamingExactContentAnalyzer()),
            async (attempt, forward) =>
            {
                if (attempt != 1) return await forward();
                acknowledgmentEntered.TrySetResult();
                await releaseAcknowledgment.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                var result = await forward();
                staleResult.TrySetResult(result);
                return result;
            });
        await using var scan = await ExactScanFixture.CreateAsync(corpus.Root, executor);

        try
        {
            await scan.StartAsync(ruleRevision: 1);
            await Bound(acknowledgmentEntered.Task);
            Assert.Equal(1, reader.Live);
            Assert.Throws<IOException>(() =>
            {
                using var writer = new FileStream(
                    PathFor(corpus, "unique/abc.jpg"), FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            });
            await scan.StartAsync(ruleRevision: 2);
        }
        finally
        {
            releaseAcknowledgment.TrySetResult();
        }

        Assert.False((await Bound(staleResult.Task)).Accepted);
        var snapshot = await scan.WaitForTerminalAsync();
        await scan.SettleAsync();
        Assert.Equal(ScanStatus.Complete, snapshot.Status);
        Assert.Equal(2, snapshot.Pin!.RuleSetRevision);
        Assert.Equal(0, reader.Live);
    }

    [Theory]
    [InlineData(MetadataProjection.UnknownAllocation, RecoveryUnavailableReason.UnknownAllocation)]
    [InlineData(MetadataProjection.UnstableAllocation, RecoveryUnavailableReason.InconsistentFacts)]
    public async Task RecoveryRemainsUnavailableWhenNativeMetadataCannotBeTrusted(
        MetadataProjection projection,
        RecoveryUnavailableReason expectedReason)
    {
        await using var corpus = await ExactCorpus.CreateAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken);
        var reader = new ObservedProtectedFileReader(new WindowsProtectedFileReader())
        {
            ProjectOpened = facts => Project(facts, projection, final: false),
            ProjectFinal = facts => Project(facts, projection, final: true),
        };
        var executor = new BoundedScanExecutor(
            new OrderedSubsetEnumerator(corpus.Root, ["race/a-source.jpg", "race/b-copy.png"]),
            reader,
            new StreamingExactContentAnalyzer());
        await using var scan = await ExactScanFixture.CreateAsync(corpus.Root, executor);

        await scan.StartAsync();
        var snapshot = await scan.WaitForTerminalAsync();
        await scan.SettleAsync();

        Assert.Equal(ScanStatus.Complete, snapshot.Status);
        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(expectedReason, group.PotentialRecovery.Reason);
        Assert.Null(group.PotentialRecovery.Bytes);
        Assert.Equal(0, reader.Live);
    }

    [Fact]
    public async Task CancellationAtFullWorkCapacitySettlesEveryAdmittedRowAndClosesTheLiveHandle()
    {
        await using var corpus = await ExactCorpus.CreateAsync(
            Path.GetTempPath(), TestContext.Current.CancellationToken);
        var paths = new List<string>();
        for (var index = 0; index < BoundedScanExecutor.WorkCapacity + 8; index++)
        {
            var relative = $"pressure/{index:D2}.jpg";
            paths.Add(relative);
            var bytes = new byte[1000 + index];
            bytes.AsSpan().Fill((byte)(index + 1));
            var path = PathFor(corpus, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        }
        var sourceBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(corpus.Root, "*", SearchOption.AllDirectories))
        {
            sourceBytes.Add(
                Path.GetRelativePath(corpus.Root, path),
                await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }

        var hashEntered = Signal();
        var reader = new ObservedProtectedFileReader(new WindowsProtectedFileReader());
        var registered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new InterceptingExecutor(
            new BoundedScanExecutor(
                new OrderedSubsetEnumerator(corpus.Root, paths),
                reader,
                new BlockingHashAnalyzer(new StreamingExactContentAnalyzer(), hashEntered)),
            afterRegistration: count =>
            {
                if (count == BoundedScanExecutor.WorkCapacity + 2) registered.TrySetResult(count);
            });
        await using var scan = await ExactScanFixture.CreateAsync(corpus.Root, executor);

        await scan.StartAsync();
        await Bound(hashEntered.Task);
        Assert.Equal(BoundedScanExecutor.WorkCapacity + 2, await Bound(registered.Task));
        Assert.Equal(1, reader.Live);
        await scan.CancelAsync();
        var snapshot = await scan.WaitForTerminalAsync();
        await scan.SettleAsync();

        Assert.Equal(ScanStatus.Incomplete, snapshot.Status);
        Assert.Equal(0, snapshot.Counts.Outstanding);
        Assert.Equal(BoundedScanExecutor.WorkCapacity + 2, snapshot.Counts.Discovered);
        Assert.Equal(snapshot.Counts.Discovered, snapshot.Counts.Canceled);
        Assert.Equal(0, reader.Live);
        var settledBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(corpus.Root, "*", SearchOption.AllDirectories))
        {
            settledBytes.Add(
                Path.GetRelativePath(corpus.Root, path),
                await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        Assert.Equal(
            sourceBytes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            settledBytes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var source in sourceBytes)
        {
            Assert.Equal(source.Value, settledBytes[source.Key]);
        }
    }

    private static ProtectedFileFacts Project(
        ProtectedFileFacts facts,
        MetadataProjection projection,
        bool final) => projection switch
    {
        MetadataProjection.UnknownAllocation => facts with
        {
            Allocation = facts.Allocation with { AllocatedBytes = null, Stable = true },
        },
        MetadataProjection.UnstableAllocation when final => facts with
        {
            Allocation = facts.Allocation with
            {
                AllocatedBytes = checked((facts.Allocation.AllocatedBytes ?? 0) + 4096),
                Stable = true,
            },
        },
        _ => facts,
    };

    private static ScanFileOccurrence Occurrence(ScanSnapshot snapshot, string relative, ExactCorpus corpus) =>
        Assert.Single(snapshot.Occurrences.Where(row => Relative(corpus, row.DiscoveredPath) == relative));

    private static CandidateGroup GroupContaining(ScanSnapshot snapshot, string relative, ExactCorpus corpus) =>
        Assert.Single(snapshot.Groups.Where(group => group.Members.Any(member =>
            Relative(corpus, member.DiscoveredPath) == relative)));

    private static string Canonical(ExactCorpus corpus, string relative) =>
        corpus.AliasTargets.TryGetValue(relative, out var target) ? target : relative;

    private static string Relative(ExactCorpus corpus, string path) =>
        Path.GetRelativePath(corpus.Root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string PathFor(ExactCorpus corpus, string relative) =>
        Path.Combine(corpus.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsSupportedSource(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    private static SourceTruth ReadSourceTruth(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var facts = new WindowsFileNative().Read(stream.SafeFileHandle, path);
        var key = facts.PhysicalFileKey
            ?? throw new InvalidDataException($"Source identity was unavailable: {path}");
        var linkCount = facts.Allocation.LinkCount
            ?? throw new InvalidDataException($"Source link count was unavailable: {path}");
        if (!facts.Allocation.Stable)
        {
            throw new InvalidDataException($"Source allocation facts were unstable: {path}");
        }
        return new SourceTruth(key, linkCount, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Bound(Task task) =>
        task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

    private static Task<T> Bound<T>(Task<T> task) =>
        task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

    public enum MetadataProjection
    {
        UnknownAllocation,
        UnstableAllocation,
    }

    private sealed record SourceTruth(
        PhysicalFileKey PhysicalFileKey,
        uint LinkCount,
        string Sha256);
}

internal static class NearQualityExactCorpusRecipes
{
    public static NearQualityExactPair CreatePair(string transformation)
    {
        if (transformation == "crop")
        {
            var cropSource = CropSource(24, 16);
            return new(cropSource, Crop(cropSource, 24, 8, 8));
        }

        var source = Gradient(24, 16);
        var right = transformation switch
        {
            "byte-identical" => source.ToArray(),
            "brightness" => Brighten(source, 8),
            "burst shift" => Shift(source, 1),
            "recompression" => Quantize(source, 16),
            "resize ambiguity" => ResizeNearest(source, 24, 16, 12, 8),
            "mirror" => Mirror(source, 24, 16),
            "rotation" => RotateClockwise(source, 24, 16),
            "unrelated" => Checkerboard(24, 16),
            "decoder failure" => [0x00],
            _ => throw new InvalidDataException($"Unknown quality transformation: {transformation}"),
        };
        return new(source, right);
    }

    private static byte[] CropSource(int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(x < 8 && y < 8
                ? ((x + y) & 1) == 0 ? 10 : 245
                : 0);
            var offset = (y * width + x) * 4;
            result[offset] = result[offset + 1] = result[offset + 2] = value;
            result[offset + 3] = 255;
        }
        return result;
    }

    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)((x * 9 + y * 5) % 220);
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(value / 2);
            pixels[offset + 1] = value;
            pixels[offset + 2] = (byte)Math.Min(255, value + 20);
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Brighten(byte[] source, byte amount)
    {
        var result = source.ToArray();
        for (var index = 0; index < result.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            result[index + channel] = (byte)Math.Min(255, result[index + channel] + amount);
        return result;
    }

    private static byte[] Shift(byte[] source, int pixels)
    {
        var result = new byte[source.Length];
        const int width = 24;
        const int height = 16;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + Math.Max(0, x - pixels)) * 4,
                result, (y * width + x) * 4, 4);
        return result;
    }

    private static byte[] Quantize(byte[] source, int step)
    {
        var result = source.ToArray();
        for (var index = 0; index < result.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            result[index + channel] = (byte)(result[index + channel] / step * step);
        return result;
    }

    private static byte[] ResizeNearest(
        byte[] source, int width, int height, int outputWidth, int outputHeight)
    {
        var result = new byte[outputWidth * outputHeight * 4];
        for (var y = 0; y < outputHeight; y++)
        for (var x = 0; x < outputWidth; x++)
            Array.Copy(source, ((y * height / outputHeight) * width + x * width / outputWidth) * 4,
                result, (y * outputWidth + x) * 4, 4);
        return result;
    }

    private static byte[] Crop(byte[] source, int width, int cropWidth, int cropHeight)
    {
        var result = new byte[cropWidth * cropHeight * 4];
        for (var y = 0; y < cropHeight; y++)
        for (var x = 0; x < cropWidth; x++)
            Array.Copy(source, (y * width + x) * 4, result, (y * cropWidth + x) * 4, 4);
        return result;
    }

    private static byte[] Mirror(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + width - 1 - x) * 4,
                result, (y * width + x) * 4, 4);
        return result;
    }

    private static byte[] RotateClockwise(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            Array.Copy(source, (y * width + x) * 4,
                result, (x * height + height - 1 - y) * 4, 4);
        return result;
    }

    private static byte[] Checkerboard(int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)(((x / 2 + y / 2) & 1) == 0 ? 10 : 245);
            var offset = (y * width + x) * 4;
            result[offset] = result[offset + 1] = result[offset + 2] = value;
            result[offset + 3] = 255;
        }
        return result;
    }
}

internal sealed record NearQualityExactPair(byte[] Left, byte[] Right);

internal sealed class ExactScanFixture : IAsyncDisposable
{
    private readonly SerialTestScheduler scheduler;
    private readonly ScanCoordinator coordinator;
    private readonly SqlitePhotoSiftStores stores;
    private readonly string databasePath;
    private readonly ShellSnapshot ready;
    private readonly TaskCompletionSource<ScanSnapshot> terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? settlement;

    private ExactScanFixture(
        SerialTestScheduler scheduler,
        ScanCoordinator coordinator,
        SqlitePhotoSiftStores stores,
        string databasePath,
        ShellSnapshot ready)
    {
        this.scheduler = scheduler;
        this.coordinator = coordinator;
        this.stores = stores;
        this.databasePath = databasePath;
        this.ready = ready;
        coordinator.SnapshotChanged += OnSnapshotChanged;
    }

    public static async Task<ExactScanFixture> CreateAsync(string root, IScanExecutor executor)
    {
        var preflight = new WindowsLocalScopePreflight();
        var selection = new ScopeSelection(root, ScopeRecursion.IncludeSubfolders);
        var evidence = await preflight.InspectAsync(selection, TestContext.Current.CancellationToken);
        var result = ScopePreflightPolicy.Evaluate(evidence);
        if (result.Decision != ScopePreflightDecision.Ready)
        {
            throw new InvalidOperationException("The generated exact corpus did not pass Windows preflight.");
        }

        var scheduler = new SerialTestScheduler();
        var databasePath = Path.Combine(Path.GetTempPath(), $"photosift-exact-{Guid.NewGuid():N}.db");
        var stores = new SqlitePhotoSiftStores(databasePath);
        var coordinator = new ScanCoordinator(
            scheduler, preflight, executor, stores.ScanIdentities, stores.ScanCheckpoints);
        var ready = new ShellSnapshot([], "Exact corpus ready", ScopeSessionState.Ready,
            selection, result, Revision: 1);
        return new ExactScanFixture(scheduler, coordinator, stores, databasePath, ready);
    }

    public Task StartAsync(long ruleRevision = 1) => Bound(coordinator.StartAsync(
        ready, new ExactRuleConfirmation(ready.Revision, ruleRevision), TestContext.Current.CancellationToken));

    public Task CancelAsync() => Bound(coordinator.CancelAsync());

    public Task<ScanSnapshot> WaitForTerminalAsync() => Bound(terminal.Task);

    public Task SettleAsync() => settlement ??= Bound(coordinator.DisposeAsync().AsTask());

    private void OnSnapshotChanged(object? sender, ScanSnapshot snapshot)
    {
        if (snapshot.Status is ScanStatus.Complete or ScanStatus.Incomplete or ScanStatus.Faulted)
        {
            terminal.TrySetResult(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        coordinator.SnapshotChanged -= OnSnapshotChanged;
        await SettleAsync();
        await scheduler.DisposeAsync();
        await stores.DisposeAsync();
        File.Delete(databasePath);
    }

    private static Task Bound(Task task) => task.WaitAsync(
        TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static Task<T> Bound<T>(Task<T> task) => task.WaitAsync(
        TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}

internal sealed class SerialTestScheduler : IWorkflowScheduler, IAsyncDisposable
{
    private readonly Channel<Action> callbacks = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Task pump;

    public SerialTestScheduler() => pump = Task.Run(PumpAsync);

    public void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!callbacks.Writer.TryWrite(callback)) throw new InvalidOperationException("The test scheduler is closed.");
    }

    private async Task PumpAsync()
    {
        await foreach (var callback in callbacks.Reader.ReadAllAsync()) callback();
    }

    public async ValueTask DisposeAsync()
    {
        callbacks.Writer.TryComplete();
        await pump;
    }
}

internal sealed class OrderedSubsetEnumerator(string root, IEnumerable<string> relativePaths) : IScanEnumerator
{
    private readonly ImmutableHashSet<string> included = relativePaths
        .Select(relative => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
        .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ScanEnumerationEvent> EnumerateAsync(
        ScanRunContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var files = new List<EnumeratedFile>();
        ScanEnumerationEnded? ended = null;
        await foreach (var item in new WindowsScanEnumerator().EnumerateAsync(context, cancellationToken))
        {
            if (item is ScanFileFound found && included.Contains(Path.GetFullPath(found.File.DiscoveredPath)))
            {
                files.Add(found.File);
            }
            else if (item is ScanEnumerationEnded terminal)
            {
                ended = terminal;
            }
        }

        foreach (var file in files.OrderBy(file => file.DiscoveredPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ScanFileFound(file);
        }

        yield return ended ?? new ScanEnumerationEnded(
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault));
    }
}

internal sealed class ObservedProtectedFileReader(IProtectedFileReader inner) : IProtectedFileReader
{
    private int opens;
    private int live;

    public Func<int, ProtectedOpenRequest, CancellationToken, Task>? BeforeOpenAsync { get; init; }
    public Func<ProtectedFileFacts, ProtectedFileFacts>? ProjectOpened { get; init; }
    public Func<ProtectedFileFacts, ProtectedFileFacts>? ProjectFinal { get; init; }
    public int Live => Volatile.Read(ref live);

    public async ValueTask<IProtectedFileLease> OpenAsync(
        ProtectedOpenRequest request,
        CancellationToken cancellationToken)
    {
        var open = Interlocked.Increment(ref opens);
        if (BeforeOpenAsync is not null) await BeforeOpenAsync(open, request, cancellationToken);
        var lease = await inner.OpenAsync(request, cancellationToken);
        try
        {
            var observed = new ObservedLease(
                lease, ProjectOpened, ProjectFinal, () => Interlocked.Decrement(ref live));
            Interlocked.Increment(ref live);
            return observed;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class ObservedLease(
        IProtectedFileLease inner,
        Func<ProtectedFileFacts, ProtectedFileFacts>? projectOpened,
        Func<ProtectedFileFacts, ProtectedFileFacts>? projectFinal,
        Action disposed) : IProtectedFileLease
    {
        private int disposal;
        public Stream Content => inner.Content;
        public ProtectedFileFacts OpenedFacts { get; } =
            projectOpened?.Invoke(inner.OpenedFacts) ?? inner.OpenedFacts;
        public async ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken)
        {
            var result = await inner.RefreshAsync(cancellationToken);
            return projectFinal?.Invoke(result) ?? result;
        }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (Interlocked.Exchange(ref disposal, 1) == 0) disposed();
        }
    }
}

internal sealed class CollisionLabelAnalyzer(IExactContentAnalyzer inner) : IExactContentAnalyzer
{
    private static readonly string CollisionDigest = new('C', 64);

    public async ValueTask<StreamDigest> HashAsync(
        Stream content, long expectedLength, CancellationToken cancellationToken)
    {
        var actual = await inner.HashAsync(content, expectedLength, cancellationToken);
        return actual with { Sha256 = CollisionDigest };
    }

    public async ValueTask<StreamEquality> CompareAsync(
        Stream left, Stream right, long expectedLength, CancellationToken cancellationToken)
    {
        var actual = await inner.CompareAsync(left, right, expectedLength, cancellationToken);
        var label = new StreamDigest(expectedLength, CollisionDigest);
        return new StreamEquality(actual.Equal, label, label);
    }
}

internal sealed class BlockingHashAnalyzer(
    IExactContentAnalyzer inner,
    TaskCompletionSource entered) : IExactContentAnalyzer
{
    public async ValueTask<StreamDigest> HashAsync(
        Stream content, long expectedLength, CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await release.Task.WaitAsync(cancellationToken);
        return await inner.HashAsync(content, expectedLength, cancellationToken);
    }

    public ValueTask<StreamEquality> CompareAsync(
        Stream left, Stream right, long expectedLength, CancellationToken cancellationToken) =>
        inner.CompareAsync(left, right, expectedLength, cancellationToken);
}

internal sealed class InterceptingExecutor : IScanExecutor
{
    private readonly IScanExecutor inner;
    private readonly Func<int, Func<ValueTask<SignatureCommit>>, ValueTask<SignatureCommit>>? signature;
    private readonly Action<int>? afterRegistration;
    private int signatureAttempts;
    private int registrations;

    public InterceptingExecutor(
        IScanExecutor inner,
        Func<int, Func<ValueTask<SignatureCommit>>, ValueTask<SignatureCommit>>? signature = null,
        Action<int>? afterRegistration = null)
    {
        this.inner = inner;
        this.signature = signature;
        this.afterRegistration = afterRegistration;
    }

    public IScanRun Start(ScanRunContext context, IScanResultSink sink) =>
        inner.Start(context, new InterceptingSink(this, sink));

    private sealed class InterceptingSink(InterceptingExecutor owner, IScanResultSink inner) : IScanResultSink
    {
        public async ValueTask<OccurrenceWork?> RegisterAsync(ScanRunContext context, EnumeratedFile file)
        {
            var result = await inner.RegisterAsync(context, file);
            if (result is not null)
            {
                owner.afterRegistration?.Invoke(Interlocked.Increment(ref owner.registrations));
            }
            return result;
        }

        public ValueTask<SignatureCommit> CommitSignatureAsync(SignatureEvidence evidence)
        {
            var attempt = Interlocked.Increment(ref owner.signatureAttempts);
            return owner.signature is null
                ? inner.CommitSignatureAsync(evidence)
                : owner.signature(attempt, () => inner.CommitSignatureAsync(evidence));
        }

        public ValueTask<bool> CommitComparisonAsync(ComparisonEvidence evidence) =>
            inner.CommitComparisonAsync(evidence);
        public ValueTask<bool> FailAsync(OccurrenceToken token, StageOutcome outcome) =>
            inner.FailAsync(token, outcome);
        public ValueTask EnumerationEndedAsync(ScanRunContext context, StageOutcome outcome) =>
            inner.EnumerationEndedAsync(context, outcome);
    }
}
