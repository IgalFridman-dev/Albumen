using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Windows.Preview;

namespace Albumen.App.Shell;

public sealed class ReviewThumbnail
{
    internal ReviewThumbnail(ImageSource? image, int? pixelWidth, int? pixelHeight, string status)
    {
        Image = image;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Status = status;
        DimensionsText = pixelWidth is { } width && pixelHeight is { } height
            ? $"{width.ToString(CultureInfo.InvariantCulture)} × {height.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
    }

    public ImageSource? Image { get; }
    public int? PixelWidth { get; }
    public int? PixelHeight { get; }
    public string DimensionsText { get; }
    public string Status { get; }
    internal long ByteCost => Image is BitmapSource bitmap ? (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 : 0;

    internal static ReviewThumbnail From(ReviewPreviewResult result) =>
        result.Availability is PreviewAvailability.Available && result.Frame is { } frame
            ? new(PreviewItemViewModel.CreateImage(frame), result.SourcePixelWidth, result.SourcePixelHeight, string.Empty)
            : new(null, null, null, result.Reason);
}

// Loads small Review thumbnails through the bounded preview source. Cards that show the
// same photo share one load, a load nobody waits for any more is canceled, and finished
// thumbnails stay in a small byte-capped cache for the current Scan only.
public sealed class ReviewThumbnailLoader
{
    private readonly IReviewPreviewSource source;
    private readonly Func<long, (ScanRunContext Context, ScanFileOccurrence Occurrence)?> resolve;
    private readonly long byteCapacity;
    private readonly object gate = new();
    private readonly Dictionary<long, LinkedListNode<CacheEntry>> cache = [];
    private readonly LinkedList<CacheEntry> recency = new();
    private readonly Dictionary<(ScanPin Scan, long Id), PendingLoad> pending = [];
    private ScanPin? cachedScan;
    private long cachedBytes;

    internal ReviewThumbnailLoader(
        IReviewPreviewSource source,
        Func<long, (ScanRunContext Context, ScanFileOccurrence Occurrence)?> resolve,
        long byteCapacity = 48L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCapacity);
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        this.byteCapacity = byteCapacity;
    }

    internal int PendingCount
    {
        get { lock (gate) return pending.Count; }
    }

    public async Task<ReviewThumbnail?> LoadAsync(
        ScanOccurrenceViewModel member,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (cancellationToken.IsCancellationRequested
            || resolve(member.OccurrenceId) is not { } target)
            return null;

        var id = member.OccurrenceId;
        var key = (target.Context.Pin, id);
        PendingLoad load;
        lock (gate)
        {
            if (!Equals(cachedScan, target.Context.Pin))
            {
                cache.Clear();
                recency.Clear();
                cachedBytes = 0;
                cachedScan = target.Context.Pin;
            }
            if (cache.TryGetValue(id, out var hit))
            {
                recency.Remove(hit);
                recency.AddFirst(hit);
                return hit.Value.Thumbnail;
            }
            if (!pending.TryGetValue(key, out load!))
            {
                load = new PendingLoad();
                pending[key] = load;
                load.Task = LoadCoreAsync(target.Context, target.Occurrence, load.Cancellation.Token);
                _ = load.Task.ContinueWith(
                    (task, state) =>
                    {
                        _ = task.Exception;
                        ((CancellationTokenSource)state!).Dispose();
                    },
                    load.Cancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            load.Waiters++;
        }

        try
        {
            return await load.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (gate)
            {
                if (--load.Waiters == 0)
                {
                    if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, load))
                        pending.Remove(key);
                    if (!load.Task.IsCompleted)
                        load.Cancel();
                }
            }
        }
    }

    private async Task<ReviewThumbnail?> LoadCoreAsync(
        ScanRunContext context,
        ScanFileOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        ReviewPreviewResult? result = null;
        try
        {
            result = await source.LoadAsync(context, occurrence, cancellationToken);
            // A photo the source could not preview is remembered too, so scrolling past it
            // again does not re-read the file.
            var thumbnail = ReviewThumbnail.From(result);
            Remember(context.Pin, occurrence.Token.OccurrenceId, thumbnail);
            return thumbnail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReviewThumbnail(null, null, null, "Preview unavailable: the file could not be read.");
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            result?.Dispose();
        }
    }

    private void Remember(ScanPin scan, long id, ReviewThumbnail thumbnail)
    {
        lock (gate)
        {
            if (!Equals(cachedScan, scan) || cache.ContainsKey(id)) return;
            var cost = Math.Max(256, thumbnail.ByteCost);
            if (cost > byteCapacity) return;
            cache[id] = recency.AddFirst(new CacheEntry(id, thumbnail, cost));
            cachedBytes += cost;
            while (cachedBytes > byteCapacity && recency.Last is { } oldest)
            {
                recency.RemoveLast();
                cache.Remove(oldest.Value.Id);
                cachedBytes -= oldest.Value.Cost;
            }
        }
    }

    private sealed record CacheEntry(long Id, ReviewThumbnail Thumbnail, long Cost);

    private sealed class PendingLoad
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<ReviewThumbnail?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<ReviewThumbnail?>(null);
        public int Waiters { get; set; }

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
