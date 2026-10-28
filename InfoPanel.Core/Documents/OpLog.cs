using System;
using System.Collections.Generic;
using InfoPanel.Spec;

namespace InfoPanel.Documents;

/// <summary>
/// The commits a document applied recently, by <c>opId</c> (spec/docs/commit.md, "opId: retries"): the SHA-256 of each
/// body and the result it got, so a retry returns that result (<c>replayed: true</c>) and a reused <c>opId</c> with
/// another body is refused. Keeps the last <see cref="Capacity"/>, forgetting the oldest first in the order they were
/// applied; refused commits are never added. Once one is forgotten, <see cref="Expired"/> refuses a commit based on a
/// revision older than the window, or on one at or below the newest <c>baseRev</c> of a forgotten commit once the
/// document has moved past it (a forgotten no-op's retry). Not thread-safe: the document uses it under its lock.
/// </summary>
internal sealed class OpLog
{
    private readonly Dictionary<string, (byte[] Hash, long BaseRev, CommitResult Result)> _ops = new(StringComparer.Ordinal);
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

    /// <summary>
    /// The retry watermark: the newest <c>baseRev</c> of a commit whose <c>opId</c> was forgotten. A retry of a
    /// forgotten commit carries its <c>baseRev</c>, so it is at or below this.
    /// </summary>
    public long Watermark { get; private set; }

    public bool TryGet(string opId, out byte[] hash, out CommitResult result)
    {
        if (_ops.TryGetValue(opId, out var entry))
        {
            (hash, _, result) = entry;
            return true;
        }
        (hash, result) = ([], null!);
        return false;
    }

    /// <summary>
    /// Whether a commit whose <c>opId</c> isn't remembered, based on <paramref name="baseRev"/>, may be a retry of a
    /// forgotten one: based on a revision older than the window, or at or below the watermark while the document, at
    /// <paramref name="rev"/>, has moved past it. A commit that changed the document is caught by the first (it was
    /// based on a revision older than its result, which is no newer than the window's start); a no-op, applied at its
    /// own <c>baseRev</c>, only by the second. A commit based on the current revision is never refused.
    /// </summary>
    public bool Expired(long baseRev, long rev) => Forgot && (baseRev < WindowStart || (baseRev <= Watermark && baseRev < rev));

    public void Add(string opId, byte[] hash, long baseRev, CommitResult result)
    {
        if (!_ops.TryAdd(opId, (hash, baseRev, result))) return;
        _order.Enqueue(opId);
        while (_order.Count > Capacity)
        {
            var forgotten = _order.Dequeue();
            Watermark = Math.Max(Watermark, _ops[forgotten].BaseRev);
            _ops.Remove(forgotten);
            Forgot = true;
        }
    }
}
