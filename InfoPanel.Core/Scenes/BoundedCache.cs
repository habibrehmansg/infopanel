using System;
using System.Collections.Generic;

namespace InfoPanel.Scenes;

/// <summary>
/// A small thread-safe cache with a fixed size: once full, the least recently used entry goes out. A null value is an
/// entry like any other (a failed lookup), so it counts against the bound too.
/// </summary>
internal sealed class BoundedCache<TValue>(int limit)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, TValue Value)>> _nodes = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, TValue Value)> _order = new();

    public int Count
    {
        get
        {
            lock (_lock) return _nodes.Count;
        }
    }

    public bool TryGet(string key, out TValue value)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(string key, TValue value)
    {
        lock (_lock) AddLocked(key, value);
    }

    /// <summary>Adds <paramref name="key"/> unless it is there (then it counts as used); true when it was added.</summary>
    public bool TryAddNew(string key, TValue value)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return false;
            }
            AddLocked(key, value);
            return true;
        }
    }

    private void AddLocked(string key, TValue value)
    {
        if (_nodes.TryGetValue(key, out var existing))
        {
            _order.Remove(existing);
            _nodes.Remove(key);
        }
        _nodes[key] = _order.AddFirst((key, value));
        while (_nodes.Count > limit)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _nodes.Remove(last.Value.Key);
        }
    }
}
