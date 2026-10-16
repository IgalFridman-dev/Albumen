using System.Collections;

namespace PhotoSift.App.Shell;

public sealed class LazyReviewCollection<T> : IReadOnlyList<T>, IList
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

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => true;
    object ICollection.SyncRoot => gate;

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();

    bool IList.Contains(object? value) => value is T item && ((IList)this).IndexOf(item) >= 0;

    int IList.IndexOf(object? value)
    {
        if (value is not T item) return -1;
        var comparer = EqualityComparer<T>.Default;
        lock (gate)
        {
            foreach (var pair in materialized)
            {
                if (comparer.Equals(pair.Value, item)) return pair.Key;
            }
        }
        return -1;
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var itemIndex = 0; itemIndex < Count; itemIndex++)
            array.SetValue(this[itemIndex], index + itemIndex);
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
