using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>
/// When a workspace keeps its documents' unsaved changes (docs/2.0/data-folder.md, "Unsaved changes"): each watched
/// document is kept (<see cref="ProfileDocument.Checkpoint()"/>) <see cref="Quiet"/> after its last change, and at most
/// <see cref="Longest"/> after its first change not yet kept, so a long drag is kept every 10 s. Every change that makes a
/// generation counts (commit, undo, redo, revert). One timer and one run at a time for the whole workspace, off every
/// request path: a change only notes its time. A document that can't be kept is logged once per run and tried again at
/// its next change, or <see cref="Longest"/> later; so is a clean document whose kept changes couldn't be removed
/// (<see cref="ProfileDocument.RemovalPending"/>), until they are. Disposing stops it and waits for a run under way.
/// </summary>
internal sealed class RecoveryScheduler : IDisposable
{
    /// <summary>How long after the last change a document is kept.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>How long after its first change not yet kept a document is kept, however its changes go on.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Lock _run = new();
    private readonly Dictionary<ProfileDocument, Pending> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ProfileDocument> _warned = new(ReferenceEqualityComparer.Instance);
    private readonly ITimer _timer;
    private volatile bool _again;
    private bool _disposed;

    public RecoveryScheduler(TimeProvider time, ILogger logger)
    {
        _time = time;
        _logger = logger;
        _timer = time.CreateTimer(_ => RunDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Keeps <paramref name="document"/>'s changes from now on.</summary>
    public void Watch(ProfileDocument document) => document.Changed += OnChanged;

    /// <summary>Stops keeping <paramref name="document"/>'s changes (it was deleted).</summary>
    public void Forget(ProfileDocument document)
    {
        document.Changed -= OnChanged;
        lock (_lock)
        {
            _pending.Remove(document);
            _warned.Remove(document);
        }
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (sender is ProfileDocument document
            && (e.Change is DocumentChange.Commit or DocumentChange.Undo or DocumentChange.Redo or DocumentChange.Revert
                || (e.Change is DocumentChange.Saved && document.RemovalPending)))
            Note(document);
    }

    /// <summary>
    /// A change to <paramref name="document"/>, or a removal of what it kept that failed (a save's, or one at open): it is
    /// kept, or the removal tried again, <see cref="Quiet"/> from now, or sooner when its first change not yet kept is older.
    /// </summary>
    public void Note(ProfileDocument document)
    {
        var now = _time.GetTimestamp();
        lock (_lock)
        {
            if (_disposed)
                return;
            if (_pending.TryGetValue(document, out var pending))
                pending.Due = Math.Min(now + Ticks(Quiet), pending.First + Ticks(Longest));
            else
                _pending[document] = new Pending(now, now + Ticks(Quiet));
            Arm(now);
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.Ticks * (double)_time.TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>Sets the timer to the earliest due time. Under the lock.</summary>
    private void Arm(long now)
    {
        if (_disposed)
            return;
        if (_pending.Count == 0)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var due = _pending.Values.Min(p => p.Due);
        var wait = due <= now ? TimeSpan.Zero : _time.GetElapsedTime(now, due);
        _timer.Change(wait, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Keeps every document that is due, one at a time; a call while a run is under way makes that run look again.</summary>
    private void RunDue()
    {
        while (true)
        {
            if (!_run.TryEnter())
            {
                _again = true;
                return;
            }

            try
            {
                do
                {
                    _again = false;
                    RunOnce();
                }
                while (_again);
            }
            finally
            {
                _run.Exit();
            }

            // A call that came between the last look and the exit.
            if (!_again)
                return;
        }
    }

    private void RunOnce()
    {
        List<ProfileDocument> due;
        var now = _time.GetTimestamp();
        lock (_lock)
        {
            if (_disposed)
                return;
            due = [.. _pending.Where(p => p.Value.Due <= now).Select(p => p.Key)];
            foreach (var document in due)
                _pending.Remove(document);
        }

        foreach (var document in due)
            Keep(document, now);

        lock (_lock)
            Arm(_time.GetTimestamp());
    }

    private void Keep(ProfileDocument document, long now)
    {
        try
        {
            document.Checkpoint();
        }
        catch (ObjectDisposedException)
        {
            // Deleted meanwhile, or the engine is stopping.
        }
        catch (Exception ex)
        {
            bool first;
            lock (_lock)
            {
                first = _warned.Add(document);
                if (!_disposed && !_pending.ContainsKey(document))
                    _pending[document] = new Pending(now, now + Ticks(Longest));
            }
            if (first)
                _logger.Warning(ex, "Could not keep the unsaved changes of profile {ProfileId}; tried again at its next change", document.Id);
        }
    }

    /// <summary>Stops the timer and waits for a run under way; nothing is kept after this returns.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
        }
        _timer.Dispose();
        _run.Enter();
        _run.Exit();
    }

    private sealed class Pending(long first, long due)
    {
        /// <summary>When the first change not yet kept came.</summary>
        public long First { get; } = first;

        /// <summary>When the document is to be kept.</summary>
        public long Due { get; set; } = due;
    }
}
