namespace PSRemoteSub.Core.Translation;

/// <summary>Fixed-capacity least-recently-used cache. Not thread-safe.</summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = [];
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new(); // first = most recent

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _map.Remove(key);
        }
        else if (_map.Count >= capacity && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
        _map[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
    }
}
