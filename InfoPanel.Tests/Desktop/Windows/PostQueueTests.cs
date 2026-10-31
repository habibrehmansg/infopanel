using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// The queue behind the Windows UI thread, with the wake injected (no Win32): one wake while none is outstanding, order
/// kept, a throwing action logged and the next one run, nothing posted during a drain lost, a modal loop inside an
/// action woken for what is posted meanwhile and for what was queued behind it, a burst of posts making one wake, and a
/// queue that never empties still returning to the message loop with at most one wake waiting there.
/// </summary>
public sealed class PostQueueTests
{
    /// <summary>
    /// The queue with its wakes going into a model of the thread's message queue: <see cref="Pump"/> dispatches one
    /// waiting wake into a drain, as the message loop (or a modal loop) does.
    /// </summary>
    private sealed class Harness
    {
        private readonly Lock _lock = new();
        private int _native;

        public Harness(bool wakeSucceeds = true)
        {
            Queue = new PostQueue(() =>
            {
                lock (_lock)
                {
                    WakeCalls++;
                    if (!WakeSucceeds)
                        return false;
                    _native++;
                    MostNative = Math.Max(MostNative, _native);
                    return true;
                }
            }, Failures.Add);
            WakeSucceeds = wakeSucceeds;
        }

        public PostQueue Queue { get; }

        public int WakeCalls { get; private set; }

        public bool WakeSucceeds { get; set; }

        public List<Exception> Failures { get; } = [];

        /// <summary>Wakes waiting in the message queue.</summary>
        public int Native
        {
            get
            {
                lock (_lock)
                    return _native;
            }
        }

        /// <summary>The most wakes ever waiting in the message queue at once.</summary>
        public int MostNative { get; private set; }

        /// <summary>Dispatches one waiting wake into a drain; false when none waits.</summary>
        public bool Pump()
        {
            lock (_lock)
            {
                if (_native == 0)
                    return false;
                _native--;
            }

            Queue.Drain();
            return true;
        }

        /// <summary>Dispatches wakes until none waits, as a message loop with nothing else to do.</summary>
        public void PumpAll()
        {
            while (Pump())
            {
            }
        }
    }

    [Fact]
    public void OneWake_PerChangeFromEmpty_AndOrderIsKept()
    {
        var h = new Harness();
        var ran = new List<int>();

        h.Queue.Post(() => ran.Add(1));
        h.Queue.Post(() => ran.Add(2));
        h.Queue.Post(() => ran.Add(3));
        Assert.Equal(1, h.WakeCalls);

        h.PumpAll();
        Assert.Equal([1, 2, 3], ran);
        Assert.Equal(0, h.Queue.Count);

        h.Queue.Post(() => ran.Add(4));
        Assert.Equal(1, h.Native);
        h.PumpAll();
        Assert.Equal([1, 2, 3, 4], ran);
    }

    [Fact]
    public void AnActionThatThrows_IsReported_AndTheNextOneRuns()
    {
        var h = new Harness();
        var ran = new List<int>();
        var boom = new InvalidOperationException("boom");

        h.Queue.Post(() => ran.Add(1));
        h.Queue.Post(() => throw boom);
        h.Queue.Post(() => ran.Add(3));
        h.PumpAll();

        Assert.Equal([1, 3], ran);
        Assert.Same(boom, Assert.Single(h.Failures));
    }

    [Fact]
    public void ActionsPostedWhileDraining_RunInTheNextDrain_NeverLost()
    {
        var h = new Harness();
        var ran = new List<string>();

        h.Queue.Post(() =>
        {
            ran.Add("a");
            h.Queue.Post(() => ran.Add("c"));
        });
        h.Queue.Post(() => ran.Add("b"));

        // A drain runs what was queued when it started, then lets the message loop go on, a wake waiting for the rest.
        Assert.True(h.Pump());
        Assert.Equal(["a", "b"], ran);
        Assert.Equal(1, h.Queue.Count);
        Assert.Equal(1, h.Native);

        h.PumpAll();
        Assert.Equal(["a", "b", "c"], ran);
        Assert.Equal(0, h.Queue.Count);

        // A drain with nothing left to do (the wake of a post the running drain already took) is harmless.
        h.Queue.Drain();
        Assert.Equal(["a", "b", "c"], ran);
    }

    [Fact]
    public void AnActionRunningAModalLoop_IsWokenForWhatIsPostedMeanwhile_InOrder()
    {
        // As a modal loop does, the action dispatches its thread's messages: a waiting wake is dispatched into a drain of
        // its own, inside the action, which runs what is queued behind the action and what is posted meanwhile.
        var h = new Harness();
        var ran = new List<string>();

        h.Queue.Post(() =>
        {
            ran.Add("modal start");
            h.Queue.Post(() => ran.Add("posted during the loop"));
            h.PumpAll(); // the loop dispatches the wake
            ran.Add("modal end");
        });
        h.Queue.Post(() => ran.Add("queued behind it"));
        h.PumpAll();

        Assert.Equal(["modal start", "queued behind it", "posted during the loop", "modal end"], ran);
    }

    [Fact]
    public void AnActionRunningAModalLoop_RunsWhatWasQueuedBehindIt_WithoutAnotherPost()
    {
        // A and B are queued under one wake. A runs a modal loop until B has run, and nothing else is posted: the wake
        // A's drain was dispatched for is spent, so B runs inside the loop only if a wake for it is waiting already.
        var h = new Harness();
        var ran = new List<string>();
        var bRan = false;

        h.Queue.Post(() =>
        {
            ran.Add("a start");
            for (var i = 0; i < 100 && !bRan; i++)
                h.Pump(); // the loop dispatches what waits
            ran.Add(bRan ? "a end, b ran inside" : "a end, b didn't run");
        });
        h.Queue.Post(() =>
        {
            bRan = true;
            ran.Add("b");
        });
        Assert.Equal(1, h.WakeCalls);
        h.PumpAll();

        Assert.Equal(["a start", "b", "a end, b ran inside"], ran);
    }

    [Fact]
    public void AQueueThatNeverEmpties_StillReturnsToTheMessageLoop_WithOneWakeWaiting()
    {
        // An animated panel's frames: each action posts the next before the drain gets back to the message loop.
        var h = new Harness();
        var ran = 0;
        void Next()
        {
            if (++ran < 1_000)
                h.Queue.Post(Next);
        }

        h.Queue.Post(Next);
        Assert.True(h.Pump());
        Assert.Equal(1, ran);
        Assert.Equal(1, h.Native);

        h.PumpAll();
        Assert.Equal(1_000, ran);
        Assert.Equal(1, h.MostNative);
    }

    [Fact]
    public async Task SustainedPostsFromSeveralThreads_KeepAtMostOneWakeWaiting()
    {
        // Four threads post for as long as the UI thread keeps draining; the message queue never holds more than one
        // wake, and every action runs once.
        var h = new Harness();
        const int threads = 4, each = 20_000;
        var count = 0;
        var posters = Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < each; i++)
                h.Queue.Post(() => count++);
        })));

        var deadline = Environment.TickCount64 + 30_000;
        while (!(posters.IsCompleted && h.Queue.Count == 0 && h.Native == 0))
        {
            Assert.True(Environment.TickCount64 < deadline, $"timed out with {count} of {threads * each} run");
            if (!h.Pump())
                Thread.Yield();
        }

        await posters;
        Assert.Equal(threads * each, count);
        Assert.Equal(1, h.MostNative);
    }

    [Fact]
    public void TenThousandPosts_MakeOneWake()
    {
        var h = new Harness();
        var count = 0;

        for (var i = 0; i < 10_000; i++)
            h.Queue.Post(() => count++);
        Assert.Equal(1, h.WakeCalls);

        h.PumpAll();
        Assert.Equal(10_000, count);
    }

    [Fact]
    public async Task PostsFromManyThreads_AreEachRunOnce()
    {
        var h = new Harness();
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++)
                h.Queue.Post(() => count++);
        })));
        Assert.Equal(1, h.WakeCalls);

        h.PumpAll();
        Assert.Equal(8_000, count);
    }

    [Fact]
    public void AWakeThatCouldntBeSent_LeavesTheActionsQueued_AndTheNextPostWakesAgain()
    {
        var h = new Harness(wakeSucceeds: false);
        var ran = new List<int>();

        h.Queue.Post(() => ran.Add(1));
        Assert.Equal(1, h.WakeCalls);
        Assert.Equal(1, h.Queue.Count);

        h.WakeSucceeds = true;
        h.Queue.Post(() => ran.Add(2));
        Assert.Equal(2, h.WakeCalls);
        h.PumpAll();
        Assert.Equal([1, 2], ran);
    }
}
