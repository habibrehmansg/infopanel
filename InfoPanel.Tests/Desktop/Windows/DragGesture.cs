using System.Runtime.InteropServices;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>The mouse a drag test injects input with: the real one through <c>SendInput</c>, or a test's stand-in.</summary>
internal interface IMouseInput
{
    (int X, int Y) Position { get; }

    bool TryMoveTo(int x, int y);

    bool TryDown();

    bool TryUp();
}

/// <summary>
/// A gesture's way to the mouse. Every input checks the gesture's cancellation first, under a gate the cleanup shuts
/// (<see cref="TryClose"/>), so once the cleanup has shut it nothing more is injected, whatever the worker still does;
/// bar, when the cleanup gave up waiting for the gate, the one input already inside the mouse.
/// </summary>
internal sealed class GestureMouse
{
    private readonly IMouseInput _mouse;
    private readonly CancellationToken _token;
    private readonly Lock _gate = new();
    private volatile bool _closed; // set under the gate, or without it when the gate stays held past the cleanup's bound
    private bool _pressed;

    internal GestureMouse(IMouseInput mouse, CancellationToken token)
    {
        _mouse = mouse;
        _token = token;
    }

    public (int X, int Y) Position => _mouse.Position;

    public void MoveTo(int x, int y) => Inject(() => _mouse.TryMoveTo(x, y), $"a move to ({x}, {y})");

    public void Down() => Inject(() =>
    {
        _pressed = true;
        return _mouse.TryDown();
    }, "a press");

    /// <summary>Waits <paramref name="milliseconds"/>, or throws as soon as the gesture is cancelled.</summary>
    public void Pause(int milliseconds)
    {
        if (_token.WaitHandle.WaitOne(milliseconds))
            _token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The gesture's own release of a button it pressed, in its finally. Once the gesture is cancelled it injects nothing:
    /// the cleanup releases the button once it has shut the gate.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_closed || _token.IsCancellationRequested || !_pressed)
                return;
            _mouse.TryUp();
            _pressed = false;
        }
    }

    /// <summary>
    /// Shuts the gate, waiting up to <paramref name="bound"/> for an input in flight. True when it got the gate: no
    /// input passes after this returns, and <paramref name="pressed"/> says whether the gesture left the button down.
    /// False when the worker still holds the gate past the bound, inside the mouse: the gate is shut without it, so no
    /// input after that one passes, but that one can still land late; <paramref name="pressed"/> is then false, since
    /// calling the mouse again could block the same way.
    /// </summary>
    internal bool TryClose(TimeSpan bound, out bool pressed)
    {
        if (_gate.TryEnter(bound))
        {
            try
            {
                _closed = true;
                pressed = _pressed;
                return true;
            }
            finally
            {
                _gate.Exit();
            }
        }

        _closed = true; // every input checks it under the gate, so the next one sees it
        pressed = false;
        return false;
    }

    private void Inject(Func<bool> send, string what)
    {
        lock (_gate)
        {
            if (_closed)
                throw new OperationCanceledException("The gesture has been cleaned up.");
            _token.ThrowIfCancellationRequested();
            if (!send())
                throw new InvalidOperationException($"Injecting {what} failed ({Marshal.GetLastPInvokeError()}): the session must be connected, not locked.");
        }
    }
}

/// <summary>
/// A drag test's gesture on a worker thread, bounded by a guard. Its cleanup, on every way out, cancels the gesture,
/// waits for the worker (up to <see cref="CleanupBound"/>), shuts the gate (waiting up to the bound again for an input
/// in flight), and only then releases a button the gesture left down; so it returns within twice the bound, and a
/// cancelled or timed-out test injects nothing once it has moved on. The one trade: when the worker is still inside
/// the mouse past both waits (<c>SendInput</c> itself blocking for seconds), the cleanup shuts the gate without it and
/// fails the test, leaving the release to the caller's cleanup (the engine stopped first); that one input can still
/// land late, nothing after it.
/// </summary>
internal static class DragGesture
{
    /// <summary>How long the cleanup waits for a cancelled worker, and then for the gate.</summary>
    internal static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(5);

    /// <param name="clock">The guard's clock (a test's own, to expire it on cue); the system's by default.</param>
    /// <param name="cleanupBound">Each of the cleanup's two waits; <see cref="CleanupBound"/> by default.</param>
    internal static async Task Run(IMouseInput mouse, Action<GestureMouse> gesture, TimeSpan guard, CancellationToken token,
        TimeProvider? clock = null, TimeSpan? cleanupBound = null)
    {
        var bound = cleanupBound ?? CleanupBound;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var input = new GestureMouse(mouse, cancel.Token);
        var worker = Task.Run(() => gesture(input), CancellationToken.None);
        // Observed whenever it ends, after the cleanup too: a cancelled worker's exception is expected.
        _ = worker.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            await worker.WaitAsync(guard, clock ?? TimeProvider.System, token);
        }
        finally
        {
            cancel.Cancel();
            await Task.WhenAny(worker, Task.Delay(bound, CancellationToken.None));
            if (!input.TryClose(bound, out var pressed))
                Assert.Fail($"The gesture's worker was still inside an input {bound.TotalSeconds:0.#} s after its cleanup began; " +
                    "its gate is shut, so nothing after that input is injected, but that one can still land. The button is left to the caller's cleanup.");
            if (pressed)
                mouse.TryUp();
        }
    }

    /// <summary>
    /// Presses the left button at (<paramref name="fromX"/>, <paramref name="fromY"/>), moves to (<paramref name="toX"/>,
    /// <paramref name="toY"/>) in ten steps, holds (running <paramref name="whileHeld"/>), and releases, in a finally.
    /// </summary>
    internal static void Drag(GestureMouse mouse, int fromX, int fromY, int toX, int toY, Action? whileHeld = null)
    {
        mouse.MoveTo(fromX, fromY);
        mouse.Pause(100);
        try
        {
            mouse.Down();
            mouse.Pause(100);
            for (var step = 1; step <= 10; step++)
            {
                mouse.MoveTo(fromX + (toX - fromX) * step / 10, fromY + (toY - fromY) * step / 10);
                mouse.Pause(30);
            }

            mouse.Pause(150);
            Assert.Equal((toX, toY), mouse.Position);
            whileHeld?.Invoke();
        }
        finally
        {
            mouse.Release();
        }
    }
}
