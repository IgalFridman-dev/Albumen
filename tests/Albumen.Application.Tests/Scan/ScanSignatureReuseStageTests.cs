using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ScanSignatureReuseStageTests
{
    private static readonly SignatureReuse NearReuse = new(new(2, 0, 0), new(1, 1, 0), SignatureReuseCounts.None);
    private static readonly SemanticSnapshot Ready =
        new(SemanticState.Ready, 2, 2, 2, "profile", "") { Vectors = new(2, 0, 0) };

    [Fact]
    public async Task TheReuseCountsOfBothStagesAreOnTheFinishedSnapshot()
    {
        await using var fixture = Fixture();

        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        var complete = fixture.Owner.Current;
        Assert.Equal(new SignatureReuse(new(2, 0, 0), new(1, 1, 0), new(2, 0, 0)), complete.Reuse);
        Assert.Equal(2, complete.Counts.Analyzed);
        Assert.Equal(2, complete.Occurrences.Count(row => row.NearDuplicate.State == ScanStageState.Succeeded));
    }

    [Fact]
    public async Task ANewScanStartsWithNoCounts()
    {
        await using var fixture = Fixture();
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        await fixture.Start(scope: 2, rules: 2);

        Assert.Equal(SignatureReuse.None, fixture.Owner.Current.Reuse);
    }

    [Fact]
    public async Task ForgettingTheIndexKeepsTheCountsOfTheScanThatRan()
    {
        await using var fixture = Fixture();
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);
        var reuse = fixture.Owner.Current.Reuse;

        await fixture.Pump(fixture.Owner.ForgetSemanticIndexAsync());

        Assert.Equal(SemanticSnapshot.Idle, fixture.Owner.Current.Semantic);
        Assert.Equal(reuse, fixture.Owner.Current.Reuse);
    }

    private static CoordinatorFixture Fixture() =>
        new(new BoundedScanExecutor(new ScanTestEnumerator(2), new ScanTestReader(), new ScanTestAnalyzer()),
            nearDuplicates: new ReusingNearPipeline(), semanticIndex: new FixedIndexer());

    // Every photo gets a signature, as if served from the store, with the counts of a partly warm Scan.
    private sealed class ReusingNearPipeline : INearDuplicatePipeline
    {
        public Task<NearDuplicateBatch> AnalyzeAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NearDuplicateBatch(context.Pin, BoundedNearDuplicatePipeline.ImplementationRevision,
                [.. rows.Select(row => new NearFeatureResult(row.Token, row.Content,
                    new PerceptualSignature(BoundedNearDuplicatePipeline.ImplementationRevision,
                        (ulong)row.Token.OccurrenceId, 100, 100, 100, 100),
                    new StageOutcome(ScanStageState.Succeeded, ScanReason.None)))])
            {
                Reuse = NearReuse,
            });
    }

    private sealed class FixedIndexer : ISemanticIndexer
    {
        public Task<SemanticSnapshot> IndexAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
            Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken) => Task.FromResult(Ready);
    }
}
