using System;
using System.Collections.Generic;

namespace InfoPanel.Editing;

/// <summary>A document's full state as XML text: the profile and its items (<c>ProfileSerializer</c> snapshots).</summary>
public sealed record DocumentSnapshot(string ProfileXml, string ItemsXml);

/// <summary>
/// One document's undo and redo stacks of <see cref="DocumentSnapshot"/>s, with coalescing:
/// <list type="bullet">
/// <item>a key starting with <c>gesture:</c> always merges into the previous entry with the same key;</item>
/// <item>any other key merges when it arrives less than <see cref="CoalesceWindow"/> after the previous
/// record with the same key (each merged record restarts the window);</item>
/// <item>a record without a key never merges.</item>
/// </list>
/// A merge keeps the older "before" snapshot, so one undo goes back to before the whole gesture or burst. Every
/// record clears the redo stack; undo and redo end any merge. At most <see cref="Limit"/> undo entries are kept.
/// Not thread-safe: the document calls it under its <c>SyncRoot</c>.
/// </summary>
public sealed class UndoHistory(TimeProvider time)
{
    public const int Limit = 100;

    public const string GesturePrefix = "gesture:";

    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(1000);

    private readonly LinkedList<DocumentSnapshot> _past = new();
    private readonly Stack<DocumentSnapshot> _future = new();
    private string? _lastKey;
    private long _lastAt;

    public bool CanUndo => _past.Count > 0;

    public bool CanRedo => _future.Count > 0;

    public int UndoCount => _past.Count;

    public int RedoCount => _future.Count;

    /// <summary>The stacks and coalescing state, for <see cref="Restore"/>.</summary>
    internal readonly record struct State(DocumentSnapshot[] Past, DocumentSnapshot[] Future, string? LastKey, long LastAt);

    /// <summary>A copy of the whole history, so a failed edit can put it back (at most <see cref="Limit"/> references per stack).</summary>
    internal State Save() => new([.. _past], _future.ToArray(), _lastKey, _lastAt);

    /// <summary>Puts back a history taken with <see cref="Save"/>.</summary>
    internal void Restore(State state)
    {
        _past.Clear();
        foreach (var entry in state.Past)
            _past.AddLast(entry);

        // ToArray lists the top first; push from the bottom up.
        _future.Clear();
        for (var i = state.Future.Length - 1; i >= 0; i--)
            _future.Push(state.Future[i]);

        _lastKey = state.LastKey;
        _lastAt = state.LastAt;
    }

    /// <summary>Records the state from before a change. Returns true when it merged into the previous entry.</summary>
    public bool Record(DocumentSnapshot before, string? key)
    {
        var now = time.GetTimestamp();
        var merge = key != null && key == _lastKey && _past.Count > 0
            && (key.StartsWith(GesturePrefix, StringComparison.Ordinal) || time.GetElapsedTime(_lastAt, now) < CoalesceWindow);

        if (!merge)
        {
            _past.AddLast(before);
            while (_past.Count > Limit)
                _past.RemoveFirst();
        }

        _future.Clear();
        _lastKey = key;
        _lastAt = now;
        return merge;
    }

    /// <summary>The state to restore, pushing <paramref name="current"/> onto the redo stack; null when there is nothing to undo.</summary>
    public DocumentSnapshot? Undo(DocumentSnapshot current)
    {
        if (_past.Last is not { } last)
            return null;

        _past.RemoveLast();
        _future.Push(current);
        _lastKey = null;
        return last.Value;
    }

    /// <summary>The state to restore, pushing <paramref name="current"/> onto the undo stack; null when there is nothing to redo.</summary>
    public DocumentSnapshot? Redo(DocumentSnapshot current)
    {
        if (!_future.TryPop(out var next))
            return null;

        _past.AddLast(current);
        while (_past.Count > Limit)
            _past.RemoveFirst();
        _lastKey = null;
        return next;
    }
}
