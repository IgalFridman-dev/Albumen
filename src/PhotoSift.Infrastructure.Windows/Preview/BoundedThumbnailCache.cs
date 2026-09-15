namespace PhotoSift.Infrastructure.Windows.Preview;

public sealed class BoundedThumbnailCache : IDisposable
{
    private readonly object sync = new();
    private readonly long byteCapacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> recency = [];
    private long ownedBytes;
    private bool disposed;

    public BoundedThumbnailCache(long byteCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCapacity);
        this.byteCapacity = byteCapacity;
    }

    public long OwnedBytes
    {
        get { lock (sync) return ownedBytes; }
    }

    public void Add(string key, PreviewFrame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(frame);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entries.Remove(key, out var existing)) Remove(existing);
            if (frame.ByteLength > byteCapacity)
            {
                frame.Dispose();
                return;
            }

            var node = recency.AddFirst(new Entry(key, frame));
            entries.Add(key, node);
            ownedBytes += frame.ByteLength;
            while (ownedBytes > byteCapacity) Remove(recency.Last!);
        }
    }

    public bool TryGet(string key, out PreviewFrame? frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out var node))
            {
                frame = null;
                return false;
            }

            recency.Remove(node);
            recency.AddFirst(node);
            frame = node.Value.Frame.Clone();
            return true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            while (recency.Last is not null) Remove(recency.Last);
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        recency.Remove(node);
        entries.Remove(node.Value.Key);
        ownedBytes -= node.Value.Frame.ByteLength;
        node.Value.Frame.Dispose();
    }

    private sealed record Entry(string Key, PreviewFrame Frame);
}
