using System.Collections;

namespace PhotoSift.App.Shell;

public sealed class LazyReviewCollection<T> : IReadOnlyList<T>
{
    private readonly Func<int, T> factory;
    private readonly Dictionary<int, T> materialized = [];
    private readonly object gate = new();

    public LazyReviewCollection(int count, Func<int, T> factory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public int Count { get; }

    public int MaterializedCount
    {
        get
        {
            lock (gate)
                return materialized.Count;
        }
    }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            lock (gate)
            {
                if (materialized.TryGetValue(index, out var value))
                    return value;

                value = factory(index);
                materialized.Add(index, value);
                return value;
            }
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
