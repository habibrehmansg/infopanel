namespace InfoPanel.Desktop.Windows;

/// <summary>
/// The work posted to a UI thread, run there in the order it was posted (<see cref="WinUiThread"/>'s queue; no Win32 in
/// it, so it is tested on any OS).
/// <list type="bullet">
/// <item>The wake (<c>wake</c>, one message posted to the thread) is outstanding from when it is sent until the thread
/// dispatches it into <see cref="Drain"/>. At most one is ever outstanding: <see cref="Post"/> (any thread) appends an
/// action and sends a wake only when none is, so the thread's message queue holds at most one, however fast actions
/// arrive.</item>
/// <item><see cref="Drain"/> (the UI thread, on the wake) runs the actions that were queued when it started, one at a
/// time, each in its own try/catch (an action that throws is handed to <c>failed</c> and the next one runs), then
/// returns to the message loop, so a queue that never empties (animated panels) still lets the thread's other messages
/// through. Actions posted meanwhile wait for the next drain.</item>
/// <item>Before it runs an action with more queued behind it, a drain makes sure a wake is outstanding for them: an
/// action that runs a modal loop (a window's move loop, a menu) dispatches that wake into a drain of its own, so the
/// actions queued behind it run inside the loop rather than after it ends, still in the order they were posted.</item>
/// <item>A wake that couldn't be sent (<c>wake</c> returns false) leaves the actions queued; the next post, or the next
/// drain, wakes again.</item>
/// </list>
/// </summary>
internal sealed class PostQueue(Func<bool> wake, Action<Exception> failed)
{
    private readonly Lock _lock = new();
    private readonly Queue<Action> _queue = new();

    /// <summary>Wakes being sent or sent and not dispatched yet: 0 or 1 (under the lock).</summary>
    private int _outstanding;

    /// <summary>Wakes sent (for tests).</summary>
    public int Wakes { get; private set; }

    /// <summary>Actions waiting (for tests).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _queue.Count;
        }
    }

    /// <summary>Appends <paramref name="action"/>; wakes the UI thread when no wake is outstanding. Any thread.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
            _queue.Enqueue(action);
        WakeIfNeeded();
    }

    /// <summary>
    /// The UI thread, on the wake: runs the actions queued when it started, in order, then returns, a wake outstanding
    /// for any left.
    /// </summary>
    public void Drain()
    {
        int budget;
        lock (_lock)
        {
            if (_outstanding > 0)
                _outstanding--; // this is the wake being dispatched
            budget = _queue.Count;
        }

        for (var taken = 0; taken < budget; taken++)
        {
            Action action;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out action!))
                    return; // a drain inside an action's modal loop ran them
            }

            // The actions behind this one get a wake of their own first: if this one runs a modal loop, the loop
            // dispatches it and they run inside. Otherwise this drain runs them, and the wake finds nothing left.
            WakeIfNeeded();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failed(ex);
            }
        }

        WakeIfNeeded();
    }

    /// <summary>Sends a wake when actions are queued and none is outstanding.</summary>
    private void WakeIfNeeded()
    {
        lock (_lock)
        {
            if (_outstanding > 0 || _queue.Count == 0)
                return;
            _outstanding++;
            Wakes++;
        }

        bool sent;
        try
        {
            sent = wake();
        }
        catch (Exception ex)
        {
            failed(ex);
            sent = false;
        }

        if (!sent)
        {
            lock (_lock)
                _outstanding--;
        }
    }
}
