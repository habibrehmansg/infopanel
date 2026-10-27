using System;
using System.Collections.Generic;
using System.Threading;

namespace InfoPanel.Documents;

/// <summary>
/// One undo or redo step: the content to go back (or forward) to, and the bytes it is charged. The pointer is cleared
/// when the process budget evicts the step (<see cref="HistoryBudget"/>).
/// </summary>
internal sealed class HistoryEntry(GenerationContent target, long bytes)
{
    private GenerationContent? _target = target;

    public GenerationContent? Target => Volatile.Read(ref _target);

    /// <summary>Changed under the budget's lock only.</summary>
    public long Bytes { get; set; } = bytes;

    /// <summary>The step's place in the budget's order; null once released or evicted. Under the budget's lock.</summary>
    public LinkedListNode<HistoryEntry>? Node { get; set; }

    public void Evict() => Volatile.Write(ref _target, null);
}

/// <summary>
/// A document's undo and redo stacks of generation contents (spec/docs/commit.md, "Coalescing and history"). Each step
/// points to the content before (undo) or after (redo) it, and is charged the bytes of the records it changed. Bounded
/// per document by a count (the last <c>maxSteps</c>) and by bytes (<c>maxBytes</c>, which wins), oldest first, and
/// across the process by <see cref="HistoryBudget"/>. A commit merges into the previous step when it has the identical
/// <c>coalesceKey</c> and the key starts with <c>gesture:</c> or the previous commit came less than 1 s earlier; a merged
/// step is charged the change from its target to the latest content, not every commit it merged. Undo, redo and revert
/// end any merge. Not thread-safe: the document uses it under its lock.
/// </summary>
internal sealed class History(TimeProvider time, HistoryBudget budget, int maxSteps, long maxBytes)
{
    public const string GesturePrefix = "gesture:";

    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    private readonly LinkedList<HistoryEntry> _undo = new();
    private readonly LinkedList<HistoryEntry> _redo = new();
    private string? _lastKey;
    private long _lastAt;
    private long _bytes;

    public bool CanUndo => Top(_undo) is not null;

    public bool CanRedo => Top(_redo) is not null;

    public int UndoCount
    {
        get
        {
            Top(_undo);
            return _undo.Count;
        }
    }

    public int RedoCount
    {
        get
        {
            Top(_redo);
            return _redo.Count;
        }
    }

    /// <summary>The bytes charged to this document's steps (evicted ones included until it next looks).</summary>
    public long Bytes => _bytes;

    /// <summary>
    /// Records a change from <paramref name="before"/> to <paramref name="after"/> charged <paramref name="bytes"/>, or
    /// merges it into the previous step when the coalescing rules say so: that step then goes back from
    /// <paramref name="after"/> to its own target, and is charged that change instead of what it was charged.
    /// </summary>
    public void Record(GenerationContent before, GenerationContent after, long bytes, string? key)
    {
        var now = time.GetTimestamp();
        var top = Top(_undo);
        var merge = key is not null && key == _lastKey && top?.Target is not null
            && (key.StartsWith(GesturePrefix, StringComparison.Ordinal) || time.GetElapsedTime(_lastAt, now) < CoalesceWindow);
        Clear(_redo);
        if (!merge || top!.Target is not { } target || budget.Recharge(top, ChangeSet.Diff(after, target).Bytes) is not { } delta)
            Push(_undo, new HistoryEntry(before, bytes));
        else _bytes += delta;
        _lastKey = key;
        _lastAt = now;
        Trim();
    }

    /// <summary>Ends any merge: the next commit starts a new step, whatever its key (an undo or redo was asked for).</summary>
    public void EndMerge() => _lastKey = null;

    /// <summary>The content an undo goes back to, without taking it; null when there is nothing to undo.</summary>
    public GenerationContent? PeekUndo() => Top(_undo)?.Target;

    public GenerationContent? PeekRedo() => Top(_redo)?.Target;

    /// <summary>Takes the undo step (after <see cref="PeekUndo"/>) and records <paramref name="current"/> as the redo step.</summary>
    public void Undo(GenerationContent current, long bytes) => Step(_undo, _redo, current, bytes);

    public void Redo(GenerationContent current, long bytes) => Step(_redo, _undo, current, bytes);

    /// <summary>Releases every step (the document is closed).</summary>
    public void Clear()
    {
        Clear(_undo);
        Clear(_redo);
        _lastKey = null;
    }

    private void Step(LinkedList<HistoryEntry> from, LinkedList<HistoryEntry> to, GenerationContent current, long bytes)
    {
        var entry = from.Last!.Value;
        from.RemoveLast();
        Release(entry);
        Push(to, new HistoryEntry(current, bytes));
        _lastKey = null;
        Trim();
    }

    private void Push(LinkedList<HistoryEntry> stack, HistoryEntry entry)
    {
        stack.AddLast(entry);
        _bytes += entry.Bytes;
        budget.Add(entry);
    }

    /// <summary>The top step, after dropping evicted ones: a step the budget evicted ends the stack there.</summary>
    private HistoryEntry? Top(LinkedList<HistoryEntry> stack)
    {
        for (var node = stack.Last; node is not null; node = node.Previous)
        {
            if (node.Value.Target is not null) continue;
            // Everything from the evicted step down is unreachable.
            while (stack.First is { } first)
            {
                stack.RemoveFirst();
                Release(first.Value);
                if (ReferenceEquals(first, node)) break;
            }
            break;
        }
        return stack.Last?.Value;
    }

    private void Trim()
    {
        while (_undo.Count > maxSteps) DropOldest(_undo);
        while (_bytes > maxBytes && (_undo.Count > 0 || _redo.Count > 0)) DropOldest(_undo.Count > 0 ? _undo : _redo);
    }

    private void DropOldest(LinkedList<HistoryEntry> stack)
    {
        var entry = stack.First!.Value;
        stack.RemoveFirst();
        Release(entry);
    }

    private void Clear(LinkedList<HistoryEntry> stack)
    {
        while (stack.First is not null) DropOldest(stack);
    }

    private void Release(HistoryEntry entry)
    {
        _bytes -= entry.Bytes;
        budget.Release(entry);
    }
}
