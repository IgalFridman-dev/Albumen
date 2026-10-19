using System.Collections.Concurrent;
using System.Collections.Immutable;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Application.Tests.Scan;

public sealed class BoundedNearDuplicatePipelineTests
{
    [Fact]
    public async Task OneFeatureFailureIsIsolatedAndTheSingleBoundedWorkerReturnsEveryRow()
    {
        var context = ScanTestData.Context();
        var store = new ScanStore(context);
        for (var id = 1; id <= 40; id++)
        {
            var work = StoreFixture.Register(store, id);
            Assert.True(store.CommitSignature(StoreFixture.Signature(work)).Accepted);
        }
        var rows = StoreFixture.Snapshot(store).Occurrences;
        var source = new TrackingFeatureSource(failingOccurrence: 17);
        var pipeline = new BoundedNearDuplicatePipeline(source);

        var batch = await pipeline.AnalyzeAsync(context, rows,
            TestContext.Current.CancellationToken);

        Assert.Equal(BoundedNearDuplicatePipeline.Capacity, 16);
        Assert.Equal(1, source.MaximumActive);
        Assert.Equal(rows.Select(row => row.Token), batch.Results.Select(result => result.Token));
        Assert.Equal(ScanReason.ReadFailure, batch.Results[16].Outcome.Reason);
        Assert.Equal(39, batch.Results.Count(result => result.Outcome.State == ScanStageState.Succeeded));
    }

    private sealed class TrackingFeatureSource(long failingOccurrence) : INearFeatureSource
    {
        private int active;
        public int MaximumActive { get; private set; }

        public async ValueTask<NearFeatureResult> AnalyzeAsync(
            ScanRunContext context, ScanFileOccurrence row, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref active);
            MaximumActive = Math.Max(MaximumActive, count);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (row.Token.OccurrenceId == failingOccurrence) throw new IOException("injected");
                return new(row.Token, row.Content,
                    new PerceptualSignature(BoundedNearDuplicatePipeline.ImplementationRevision,
                        (ulong)row.Token.OccurrenceId, 100, 100, 100, 100),
                    new StageOutcome(ScanStageState.Succeeded, ScanReason.None));
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
