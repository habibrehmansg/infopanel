using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// The drag tests' gesture (<see cref="DragGesture"/>) on any OS, with a stand-in mouse that records what it is sent: a
/// cancelled gesture injects nothing once its cleanup has returned, a button it left down is released once, by the
/// cleanup, and the cleanup returns within its bound even when an input blocks. Cancellations and timeouts happen on
/// handshakes with the worker, never inside a timing window. Nothing here touches the real mouse.
/// </summary>
public sealed class DragGestureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    /// <summary>How long a test waits for a handshake before it fails; never what decides a pass.</summary>
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(10);

    /// <summary>The ten steps of a drag from (10, 10) to (110, 10).</summary>
    private static readonly string[] Moves = [.. Enumerable.Range(1, 10).Select(i => $"move {10 + 10 * i},10")];

    [Fact]
    public async Task AGesture_MovesPressesDragsAndReleasesOnce()
    {
        var mouse = new RecordingMouse();
        await DragGesture.Run(mouse, m => DragGesture.Drag(m, 10, 10, 110, 10), Guard, Ct);

        string[] expected = ["move 10,10", "down", .. Moves, "up"];
        Assert.Equal(expected, mouse.Inputs);
    }

    /// <summary>
    /// Cancelled between putting the pointer in place and pressing (the first move held inside the mouse until the
    /// cancellation): no press, then or after the cleanup.
    /// </summary>
    [Fact]
    public async Task ACancelledDrag_NeverPresses_AfterItsCleanup()
    {
        var mouse = new RecordingMouse();
        mouse.HoldOn("move 10,10");
        using var cancel = new CancellationTokenSource();
        using var done = new ManualResetEventSlim();
        try
        {
            var run = DragGesture.Run(mouse, Tracked(m => DragGesture.Drag(m, 10, 10, 110, 10), done), Guard, cancel.Token);

            Assert.True(mouse.Held.Wait(Handshake, Ct), "the gesture's first move");
            cancel.Cancel();
            mouse.Let();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            mouse.CleanedUp();

            Assert.True(done.Wait(Handshake, Ct), "the worker's end");
            Assert.Equal(new[] { "move 10,10" }, mouse.Inputs);
            Assert.Empty(mouse.AfterCleanup);
        }
        finally
        {
            mouse.Let();
        }
    }

    /// <summary>
    /// Cancelled while the button is held, by a step that doesn't watch the cancellation and then tries to move: the move
    /// isn't injected, and the button is released once, by the cleanup, after the worker's own release was skipped.
    /// </summary>
    [Fact]
    public async Task ACancelledDrag_IsReleasedOnce_AfterItsWorkerHasStopped()
    {
        var mouse = new RecordingMouse();
        using var cancel = new CancellationTokenSource();
        using var held = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        try
        {
            var run = DragGesture.Run(mouse, Tracked(m => DragGesture.Drag(m, 10, 10, 110, 10, () =>
            {
                held.Set();
                cancelled.Wait(); // the test always sets it
                mouse.Note("held step ends");
                m.MoveTo(0, 0);
            }), done), Guard, cancel.Token);

            Assert.True(held.Wait(Handshake, Ct), "the button held");
            cancel.Cancel();
            cancelled.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            mouse.CleanedUp();

            Assert.True(done.Wait(Handshake, Ct), "the worker's end");
            string[] expected = ["move 10,10", "down", .. Moves, "held step ends", "up"];
            Assert.Equal(expected, mouse.Inputs);
            Assert.Empty(mouse.AfterCleanup);
        }
        finally
        {
            cancelled.Set();
        }
    }

    /// <summary>Past its guard (expired on cue, once the button is held): a timeout, and the same rule as a cancellation.</summary>
    [Fact]
    public async Task ADragPastItsGuard_TimesOut_AndInjectsNothingAfterItsCleanup()
    {
        var mouse = new RecordingMouse();
        var clock = new ManualClock();
        using var held = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        var run = DragGesture.Run(mouse, Tracked(m => DragGesture.Drag(m, 10, 10, 110, 10, () =>
        {
            held.Set();
            m.Pause(60_000);
        }), done), Guard, Ct, clock);

        Assert.True(held.Wait(Handshake, Ct), "the button held");
        clock.Expire();
        await Assert.ThrowsAsync<TimeoutException>(() => run);
        mouse.CleanedUp();

        Assert.True(done.Wait(Handshake, Ct), "the worker's end");
        string[] expected = ["move 10,10", "down", .. Moves, "up"];
        Assert.Equal(expected, mouse.Inputs);
        Assert.Empty(mouse.AfterCleanup);
    }

    /// <summary>
    /// An input that blocks inside the mouse while the button is down (as a stuck <c>SendInput</c> would), past the
    /// cancellation: the cleanup still returns within its bound, failing loudly, so the caller's cleanup stops the engine
    /// and releases the button; when the blocked input lands, nothing after it is injected.
    /// </summary>
    [Fact]
    public async Task AnInputThatBlocks_LeavesTheCleanupBounded_AndNothingAfterItIsInjected()
    {
        var bound = TimeSpan.FromMilliseconds(300);
        var mouse = new RecordingMouse();
        mouse.HoldOn("move 40,10");
        using var cancel = new CancellationTokenSource();
        using var done = new ManualResetEventSlim();
        try
        {
            var run = DragGesture.Run(mouse, Tracked(m => DragGesture.Drag(m, 10, 10, 110, 10), done), Guard, cancel.Token, cleanupBound: bound);

            Assert.True(mouse.Held.Wait(Handshake, Ct), "the third step inside the mouse");
            cancel.Cancel();
            // The input stays blocked. The cleanup's two waits are 0.6 s; failing at 10 s says it never returns.
            Assert.True(await Task.WhenAny(run, Task.Delay(Handshake, Ct)) == run, "the cleanup returned within its bound");
            var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => run);
            Assert.Contains("still inside an input", failure.Message);

            // The caller's cleanup, as WithEngineWindow's: the engine stopped, then the button released.
            mouse.Note("engine stopped");
            mouse.TryUp();

            mouse.Let(); // the blocked input lands, late
            Assert.True(done.Wait(Handshake, Ct), "the worker's end");
            string[] expected = ["move 10,10", "down", "move 20,10", "move 30,10", "engine stopped", "up", "move 40,10"];
            Assert.Equal(expected, mouse.Inputs);
        }
        finally
        {
            mouse.Let();
        }
    }

    /// <summary>The gesture, setting <paramref name="done"/> when its worker ends, however it ends.</summary>
    private static Action<GestureMouse> Tracked(Action<GestureMouse> gesture, ManualResetEventSlim done) => m =>
    {
        try
        {
            gesture(m);
        }
        finally
        {
            done.Set();
        }
    };

    private sealed class RecordingMouse : IMouseInput
    {
        private readonly Lock _lock = new();
        private readonly List<string> _inputs = [], _afterCleanup = [];
        private readonly ManualResetEventSlim _let = new();
        private (int X, int Y) _position;
        private bool _cleanedUp;
        private string? _holdOn;

        /// <summary>Set when the input named by <see cref="HoldOn"/> is inside the mouse.</summary>
        public ManualResetEventSlim Held { get; } = new();

        public List<string> Inputs
        {
            get
            {
                lock (_lock)
                    return [.. _inputs];
            }
        }

        public List<string> AfterCleanup
        {
            get
            {
                lock (_lock)
                    return [.. _afterCleanup];
            }
        }

        public (int X, int Y) Position
        {
            get
            {
                lock (_lock)
                    return _position;
            }
        }

        /// <summary>
        /// The first <paramref name="input"/> stays inside the mouse (the worker in it, holding the gesture's gate) until
        /// <see cref="Let"/>, and is recorded when it returns, as it lands.
        /// </summary>
        public void HoldOn(string input) => _holdOn = input;

        public void Let() => _let.Set();

        public bool TryMoveTo(int x, int y)
        {
            var input = $"move {x},{y}";
            Hold(input);
            lock (_lock)
                _position = (x, y);
            return Record(input);
        }

        public bool TryDown()
        {
            Hold("down");
            return Record("down");
        }

        public bool TryUp()
        {
            Hold("up");
            return Record("up");
        }

        /// <summary>The test's own mark in the sequence; not an input.</summary>
        public void Note(string what)
        {
            lock (_lock)
                _inputs.Add(what);
        }

        /// <summary>The cleanup has returned: any input from now on is one too many.</summary>
        public void CleanedUp()
        {
            lock (_lock)
                _cleanedUp = true;
        }

        private void Hold(string input)
        {
            lock (_lock)
            {
                if (input != _holdOn)
                    return;
                _holdOn = null;
            }

            Held.Set();
            _let.Wait(); // the test always lets it go
        }

        private bool Record(string input)
        {
            lock (_lock)
            {
                _inputs.Add(input);
                if (_cleanedUp)
                    _afterCleanup.Add(input);
            }

            return true;
        }
    }

    /// <summary>A clock whose one timer (the guard's) fires only when the test says.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            (_callback, _state) = (callback, state);
            return new Timer();
        }

        public void Expire() => _callback!(_state);

        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}
