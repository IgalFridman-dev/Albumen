using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class ScanSemanticStageTests
{
    private static readonly SemanticSnapshot Indexing = new(SemanticState.Indexing, 2, 0, 0, "profile", "");
    private static readonly SemanticSnapshot Ready = new(SemanticState.Ready, 2, 2, 2, "profile", "");

    [Fact]
    public async Task TheIndexRunsAfterTheNearStageAndTheScanCompletesWithItsResult()
    {
        var near = new RecordingNearPipeline();
        var indexer = new RecordingSemanticIndexer();
        var nearCallsWhenIndexed = -1;
        indexer.Index = async (rows, progress, _) =>
        {
            nearCallsWhenIndexed = near.Calls;
            Assert.Equal(2, rows.Length);
            await progress(Indexing);
            return Ready;
        };
        await using var fixture = Fixture(near, indexer);
        var indexing = ScanTestData.Signal<ScanSnapshot>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Semantic.State == SemanticState.Indexing) indexing.TrySetResult(snapshot);
        };

        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        Assert.Equal(1, nearCallsWhenIndexed);
        var during = await indexing.Task;
        Assert.Equal(ScanStatus.Running, during.Status);
        var complete = fixture.Owner.Current;
        Assert.Equal(Ready, complete.Semantic);
        Assert.Equal(during.Counts, complete.Counts);
        Assert.Equal(during.Occurrences, complete.Occurrences);
        Assert.Equal(during.Groups.Select(group => group.GroupId), complete.Groups.Select(group => group.GroupId));
    }

    [Fact]
    public async Task WithLookAlikesOffThereIsNoIndex()
    {
        var indexer = new RecordingSemanticIndexer();
        await using var fixture = Fixture(new RecordingNearPipeline(), indexer);

        await fixture.Pump(fixture.Owner.StartAsync(fixture.Ready(), new ExactRuleConfirmation(1, 1,
            new ScanOptions(ExactDuplicateMode.Enabled, NearDuplicateMode.Disabled)), CancellationToken.None));
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);

        Assert.Equal(0, indexer.Calls);
        Assert.Equal(SemanticSnapshot.Idle, fixture.Owner.Current.Semantic);
    }

    [Fact]
    public async Task CancelWhileIndexingSkipsOnlyTheRestOfTheIndex()
    {
        var partial = new SemanticSnapshot(SemanticState.Ready, 2, 1, 1, "profile", "");
        var indexer = new RecordingSemanticIndexer
        {
            Index = async (_, progress, token) =>
            {
                await progress(Indexing);
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                return partial;
            },
        };
        await using var fixture = Fixture(new RecordingNearPipeline(), indexer);
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Semantic.State == SemanticState.Indexing);
        var before = fixture.Owner.Current;

        await fixture.Pump(fixture.Owner.CancelAsync());
        await fixture.Until(snapshot => snapshot.Status is ScanStatus.Complete or ScanStatus.Incomplete);

        var after = fixture.Owner.Current;
        Assert.Equal(ScanStatus.Complete, after.Status);
        Assert.Equal(partial, after.Semantic);
        Assert.Equal(before.Counts, after.Counts);
        Assert.Equal(0, after.Counts.Canceled);
    }

    [Fact]
    public async Task AnIndexerFaultIsSemanticFailedAndTheScanStillCompletes()
    {
        var indexer = new RecordingSemanticIndexer
        {
            Index = (_, _, _) => throw new InvalidOperationException("bug"),
        };
        await using var fixture = Fixture(new RecordingNearPipeline(), indexer);

        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status is ScanStatus.Complete or ScanStatus.Faulted);

        Assert.Equal(ScanStatus.Complete, fixture.Owner.Current.Status);
        Assert.Equal(SemanticState.Failed, fixture.Owner.Current.Semantic.State);
        Assert.Equal(2, fixture.Owner.Current.Counts.Analyzed);
        Assert.Single(fixture.Owner.Current.Groups);
    }

    [Fact]
    public async Task ForgettingTheIndexReturnsASettledScanToNotPreparedAndKeepsItsResults()
    {
        await using var fixture = Fixture(new RecordingNearPipeline(), new RecordingSemanticIndexer());
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);
        var complete = fixture.Owner.Current;

        await fixture.Pump(fixture.Owner.ForgetSemanticIndexAsync());

        var forgotten = fixture.Owner.Current;
        Assert.Equal(SemanticSnapshot.Idle, forgotten.Semantic);
        Assert.Equal(complete with { Semantic = SemanticSnapshot.Idle }, forgotten);
    }

    [Fact]
    public async Task ANewScanStartsNotPrepared()
    {
        await using var fixture = Fixture(new RecordingNearPipeline(), new RecordingSemanticIndexer());
        await fixture.Start();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);
        Assert.Equal(Ready, fixture.Owner.Current.Semantic);

        await fixture.Start(scope: 2, rules: 2);

        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
        Assert.Equal(SemanticSnapshot.Idle, fixture.Owner.Current.Semantic);
    }

    private static CoordinatorFixture Fixture(INearDuplicatePipeline near, ISemanticIndexer indexer) =>
        new(new BoundedScanExecutor(new ScanTestEnumerator(2), new ScanTestReader(), new ScanTestAnalyzer()),
            nearDuplicates: near, semanticIndex: indexer);

    private sealed class RecordingSemanticIndexer : ISemanticIndexer
    {
        public int Calls { get; private set; }

        public Func<ImmutableArray<ScanFileOccurrence>, Func<SemanticSnapshot, Task>, CancellationToken,
            Task<SemanticSnapshot>> Index { get; set; } = (_, _, _) => Task.FromResult(Ready);

        public Task<SemanticSnapshot> IndexAsync(ScanRunContext context, ImmutableArray<ScanFileOccurrence> rows,
            Func<SemanticSnapshot, Task> progress, CancellationToken cancellationToken)
        {
            Calls++;
            return Index(rows, progress, cancellationToken);
        }
    }
}
