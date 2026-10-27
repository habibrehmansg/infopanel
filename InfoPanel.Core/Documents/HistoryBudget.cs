using System.Collections.Generic;
using System.Threading;

namespace InfoPanel.Documents;

/// <summary>
/// The process's undo history budget (spec/docs/limits.md, process budgets: 256 MiB for all documents together): every
/// document's history steps in the order they were recorded, oldest evicted first when the total is over. Has its own
/// lock, taken only after a document's (never the other way round): eviction clears the step's pointer and never takes
/// a document's lock, and the document drops the step when it next looks.
/// </summary>
public sealed class HistoryBudget(long maxBytes)
{
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>The process's budget, shared by every document that isn't given another.</summary>
    public static HistoryBudget Shared { get; } = new(DefaultMaxBytes);

    private readonly Lock _lock = new();
    private readonly LinkedList<HistoryEntry> _entries = new();
    private long _used;

    public long MaxBytes { get; } = maxBytes;

    public long Used
    {
        get { lock (_lock) return _used; }
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Charges a new step, evicting the oldest steps (its own included) while over the budget.</summary>
    internal void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            entry.Node = _entries.AddLast(entry);
            _used += entry.Bytes;
            Trim();
        }
    }

    /// <summary>
    /// Charges a step <paramref name="bytes"/> instead of what it was charged (a commit merged into it); returns the
    /// difference, or null when the step was already evicted.
    /// </summary>
    internal long? Recharge(HistoryEntry entry, long bytes)
    {
        lock (_lock)
        {
            if (entry.Node is null) return null;
            var delta = bytes - entry.Bytes;
            entry.Bytes = bytes;
            _used += delta;
            Trim();
            return delta;
        }
    }

    /// <summary>Releases a step the document dropped (no-op when it was evicted).</summary>
    internal void Release(HistoryEntry entry)
    {
        lock (_lock)
        {
            if (entry.Node is not { } node) return;
            _entries.Remove(node);
            entry.Node = null;
            _used -= entry.Bytes;
        }
    }

    private void Trim()
    {
        while (_used > MaxBytes && _entries.First is { } oldest)
        {
            _entries.RemoveFirst();
            oldest.Value.Node = null;
            _used -= oldest.Value.Bytes;
            oldest.Value.Evict();
        }
    }
}
