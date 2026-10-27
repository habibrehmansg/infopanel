using System;
using System.Collections.Generic;
using InfoPanel.Spec;

namespace InfoPanel.Documents;

/// <summary>
/// The commits a document applied recently, by <c>opId</c> (spec/docs/commit.md, "opId: retries"): the SHA-256 of each
/// body and the result it got, so a retry returns that result (<c>replayed: true</c>) and a reused <c>opId</c> with
/// another body is refused. Keeps the last <see cref="Capacity"/>, forgetting the oldest first in the order they were
/// applied; refused commits are never added. Once one is forgotten, <see cref="Expired"/> refuses a commit based on a
/// revision older than the window. Not thread-safe: the document uses it under its lock.
/// </summary>
internal sealed class OpLog
{
    private readonly Dictionary<string, (byte[] Hash, CommitResult Result)> _ops = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public OpLog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _ops.Count;

    /// <summary>Whether any <c>opId</c> has been forgotten: until then no commit is refused as expired.</summary>
    public bool Forgot { get; private set; }

    /// <summary>The start of the retry window: the result <c>rev</c> of the oldest <c>opId</c> remembered.</summary>
    public long WindowStart => _ops[_order.Peek()].Result.Rev;

    public bool TryGet(string opId, out byte[] hash, out CommitResult result)
    {
        if (_ops.TryGetValue(opId, out var entry))
        {
            (hash, result) = entry;
            return true;
        }
        (hash, result) = ([], null!);
        return false;
    }

    /// <summary>Whether a commit whose <c>opId</c> isn't remembered, based on <paramref name="baseRev"/>, is older than the window.</summary>
    public bool Expired(long baseRev) => Forgot && baseRev < WindowStart;

    public void Add(string opId, byte[] hash, CommitResult result)
    {
        if (!_ops.TryAdd(opId, (hash, result))) return;
        _order.Enqueue(opId);
        while (_order.Count > Capacity)
        {
            _ops.Remove(_order.Dequeue());
            Forgot = true;
        }
    }
}
