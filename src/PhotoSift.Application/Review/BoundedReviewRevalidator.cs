using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Review;

public sealed class BoundedReviewRevalidator : IReviewRevalidationExecutor
{
    private readonly ProtectedReviewWorker worker;
    private readonly int maxConcurrency;
    private readonly int capacity;
    public BoundedReviewRevalidator(IProtectedFileReader reader, IExactContentAnalyzer analyzer, int maxConcurrency = 4, int capacity = 16)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        worker = new ProtectedReviewWorker(reader, analyzer);
        this.maxConcurrency = maxConcurrency;
        this.capacity = capacity;
    }

    public IReviewRun Start(ReviewSessionPin session, ScanRunContext context, ImmutableArray<CandidateGroup> groups, IReviewResultSink sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sink);
        if (groups.IsDefault) throw new ArgumentException("Review groups must be initialized.", nameof(groups));
        return new ReviewRun(session, context, groups, sink, worker, maxConcurrency, capacity);
    }
}
