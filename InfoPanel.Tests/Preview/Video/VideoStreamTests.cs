using System.Diagnostics;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The H.264 pipeline over a scriptable backend (<see cref="FakeH264Backend"/>) and a clock the test moves
/// (<see cref="ManualTime"/>), so it runs on any machine: drop before encoding, the output bound, order, generations, the
/// watchdog, bounded retirement and quarantine, one outcome per submission, the failure policy and its classes, the size
/// rule, keyframe requests, live properties and pixel/header pairing.
/// </summary>
public class VideoStreamTests
{
    private const int W = 64;
    private const int H = 48;

    /// <summary>A frame interval at 30 frames a second.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);

    /// <summary>A stream over fake backends, fed as the sink feeds it: each frame routed, then offered or sent as PNG.</summary>
    private sealed class Harness : IDisposable
    {
        private long _tick;
        private int _refreshes;

        public Harness(bool software = false, H264Backends? backends = null, FakeH264Backend? hardware = null)
        {
            Hardware = hardware ?? new FakeH264Backend("Hardware");
            Software = software ? new FakeH264Backend("Software", H264Kind.Software) : null;
            Backends = backends ?? new H264Backends(Software == null ? [Hardware] : [Hardware, Software], NullLogger.Instance);
            Stream = NewStream();
        }

        public ManualTime Time { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeH264Backend Hardware { get; }
        public FakeH264Backend? Software { get; }
        public H264Backends Backends { get; }
        public SkipCounter Skipped { get; } = new();
        public VideoStream Stream { get; private set; }
        public int Refreshes => Volatile.Read(ref _refreshes);
        public List<(long Tick, string Fallback, TimeSpan At)> Pngs { get; } = [];
        public List<EncodedFrame> Sent { get; } = [];

        public VideoStream NewStream() => new(Backends, 30, () => { }, () => Interlocked.Increment(ref _refreshes), Skipped, NullLogger.Instance, Time);

        public void Replace(VideoStream stream) => Stream = stream;

        /// <summary>
        /// The next frame (its pixels all its tick's low byte): to the encoder, or recorded as PNG with its reason, which is
        /// returned; a PNG frame the stream also asks for (a waiting episode's) is offered too, after it, as the sink does.
        /// </summary>
        public string? Frame(int width = W, int height = H)
        {
            var tick = ++_tick;
            var fallback = Stream.Route(width, height, out var offer);
            if (fallback != null)
                Pngs.Add((tick, fallback, Now));
            if (fallback != null && !offer)
                return fallback;

            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)tick);
            Stream.Offer(pixels, Header(tick, width, height));
            return fallback;
        }

        public long Tick => _tick;

        /// <summary>The clock's time since the start.</summary>
        public TimeSpan Now => TimeSpan.FromTicks(Time.GetTimestamp());

        /// <summary>
        /// A panel changing at 30 frames a second until <paramref name="until"/>: each frame interval the clock moves on,
        /// <paramref name="each"/> runs, a frame is routed and offered, and the send loop takes what is queued (unless not
        /// <paramref name="take"/>).
        /// </summary>
        public async Task Play(TimeSpan until, Action? each = null, int width = W, int height = H, bool take = true)
        {
            while (Now < until)
            {
                Time.Advance(Interval);
                each?.Invoke();
                Frame(width, height);
                await Task.Delay(5, TestContext.Current.CancellationToken);
                if (take)
                    Take();
            }
        }

        /// <summary>The render loop of a panel that doesn't change: a frame for each fresh frame asked for since the last answer.</summary>
        public void Answer()
        {
            while (Refreshes > _answered)
            {
                _answered++;
                Frame();
            }
        }

        private int _answered;

        /// <summary>A panel that doesn't change until <paramref name="until"/>: each frame interval the clock moves on, <paramref name="each"/> runs, the fresh frames asked for are drawn, and the send loop takes what is queued.</summary>
        public async Task Still(TimeSpan until, Action? each = null)
        {
            while (Now < until)
            {
                Time.Advance(Interval);
                each?.Invoke();
                Answer();
                await Task.Delay(5, TestContext.Current.CancellationToken);
                Take();
            }
        }

        /// <summary>Takes every queued frame, as the send loop does.</summary>
        public List<EncodedFrame> Take()
        {
            var taken = new List<EncodedFrame>();
            while (Stream.TryTake(out var frame))
                taken.Add(frame);
            Sent.AddRange(taken);
            return taken;
        }

        public void Dispose() => Stream.Dispose();
    }

    private static FrameHeader Header(long tick, int width, int height) => new()
    {
        ProfileId = "3dd19c0e-675a-4591-b096-83417853861f",
        Rev = 1,
        Tick = tick,
        FrameId = tick,
        Encoding = "png",
        Scale = 1,
        PixelWidth = width,
        PixelHeight = height,
        ProfileWidth = width,
        ProfileHeight = height,
        ItemBounds = [],
        EncodeMs = 0,
        Skipped = 0,
    };

    /// <summary>The tick whose pixels a fake access unit carries (its byte after the 8-byte frame id).</summary>
    private static byte PixelTick(EncodedFrame frame) => frame.Payload.Span[8];

    /// <summary>Waits (real time, at most 10 s) until <paramref name="condition"/>; with <paramref name="time"/>, moves it on 50 ms a poll.</summary>
    private static async Task Eventually(Func<bool> condition, string what, ManualTime? time = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail("Timed out waiting until " + what);
            time?.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Lets the loop run (real time) when the test expects nothing to happen.</summary>
    private static Task Settle() => Task.Delay(100, TestContext.Current.CancellationToken);

    // ---- drop before encoding, the bound, order ----

    [Fact]
    public async Task TenFramesWhileTheEncoderIsFull_OnlyTheNewestIsSubmitted_AndNoEncodedFrameIsDropped()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Hold = true;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "the first frame is submitted");
        h.Frame();
        await Eventually(() => h.Hardware.Last.Submitted.Count == 2, "the second frame is submitted");
        for (var i = 0; i < 10; i++)
            h.Frame();
        await Settle();
        Assert.Equal(2, h.Hardware.Last.Submitted.Count);
        Assert.Equal(9, h.Skipped.Peek);

        h.Hardware.Last.Release();
        await Eventually(() => h.Hardware.Last.Submitted.Count == 3, "the newest frame is submitted");
        h.Hardware.Last.Release();
        await Eventually(() => h.Stream.Queued == 3, "three frames are queued");
        var sent = h.Take();
        Assert.Equal([1, 2, 12], sent.Select(f => f.Header.Tick));
        Assert.Equal([1, 2, 12], sent.Select(f => (long)PixelTick(f)));
        Assert.Equal(9, h.Skipped.Peek);
    }

    [Fact]
    public async Task AStalledSocket_QueuesAtMostEight_AndTheEncoderHoldsAtMostTwo()
    {
        using var h = new Harness();
        for (var i = 0; i < 12; i++)
        {
            h.Frame();
            var expected = Math.Min(i + 1, VideoStream.QueueSlots);
            await Eventually(() => h.Stream.Queued == expected, $"{expected} frames are queued");
        }

        await Settle();
        Assert.Equal(VideoStream.QueueSlots, h.Stream.Occupied);
        Assert.Equal(VideoStream.QueueSlots, h.Hardware.Last.Submitted.Count);

        var sent = h.Take();
        Assert.Equal(VideoStream.QueueSlots, sent.Count);
        Assert.Equal(sent.Select(f => f.Header.Tick).Order(), sent.Select(f => f.Header.Tick));
        // The newest raw frame waited at the slot and goes once there is room.
        await Eventually(() => h.Stream.Queued == 1, "the waiting frame is encoded");
        Assert.Equal(12, h.Take().Single().Header.Tick);

        using var held = new Harness();
        held.Hardware.Configure = s => s.Hold = true;
        for (var i = 0; i < 5; i++)
        {
            held.Frame();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Settle();
        Assert.Equal(VideoStream.MaxInFlight, held.Hardware.Last.Submitted.Count);
        Assert.Equal(VideoStream.MaxInFlight, held.Stream.InFlight);
    }

    [Fact]
    public async Task AnOutOfOrderOutcome_IsAFailure_AndNothingOfThatSessionIsSent()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Hold = true;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "one submitted");
        h.Frame();
        await Eventually(() => h.Hardware.Last.Submitted.Count == 2, "two submitted");
        var first = h.Hardware.Last;
        first.Swap = true;
        first.Release();
        await Eventually(() => first.Aborted && h.Refreshes > 0, "the session is replaced");
        Assert.Empty(h.Take());
        Assert.Equal(0, h.Stream.Occupied);

        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 2 && h.Hardware.Last.Submitted.Count == 1, "a new session takes a frame");
        h.Hardware.Last.Release();
        await Eventually(() => h.Stream.Queued == 1, "queued");
        var next = Assert.Single(h.Take());
        Assert.True(next.Header.Keyframe);
    }

    [Fact]
    public async Task AnOutcomeForAFrameItWasNeverGiven_IsAFailure()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Unknown = h.Hardware.Sessions.Count == 0;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted && h.Refreshes == 1, "the session is replaced");
        Assert.Equal(0, h.Stream.Occupied);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the next session encodes");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
    }

    // ---- generations ----

    [Fact]
    public async Task NoFrameOfAnAbortedSession_FollowsTheNewSessionsKeyframe()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Hold = h.Hardware.Sessions.Count == 0;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "submitted");
        h.Frame();
        await Eventually(() => h.Hardware.Last.Submitted.Count == 2, "two submitted");
        var first = h.Hardware.Last;
        first.Release(1);
        await Eventually(() => h.Stream.Queued == 1, "the first is queued");
        first.Fail = 1;
        first.Release();
        await Eventually(() => first.Aborted && h.Refreshes == 1, "the session is aborted, a fresh frame asked for");

        h.Frame();
        // The first frame is still queued (of the aborted session's generation), the new keyframe behind it.
        await Eventually(() => h.Hardware.Sessions.Count == 2 && h.Stream.Queued == 2, "the new session encodes");
        var sent = h.Take();
        var frame = Assert.Single(sent);
        Assert.Equal(3, frame.Header.Tick);
        Assert.True(frame.Header.Keyframe);
    }

    // ---- the residuals ----

    [Fact]
    public async Task CallbackThrows_ReleasesItsSlot_AndTheStreamRecovers()
    {
        using var h = new Harness();
        var throws = 10;
        h.Stream.BeforeCommit = _ =>
        {
            if (Interlocked.Decrement(ref throws) >= 0)
                throw new InvalidOperationException("a frame that can't be built");
        };

        for (var i = 1; i <= 10; i++)
        {
            h.Frame();
            var n = i;
            await Eventually(() => h.Hardware.Sessions.Count == n && h.Hardware.Last.Aborted && h.Refreshes == n, $"failure {n} is handled");
            Assert.Equal(0, h.Stream.Occupied);
            // Spread over time, so no three fall in one window: each is transient.
            h.Time.Advance(VideoStream.FailureWindow + TimeSpan.FromSeconds(1));
        }

        Assert.Empty(h.Take());
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the stream recovers");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(11, h.Hardware.Sessions.Count);
    }

    [Fact]
    public async Task LostCallback_OnAStillPanel_RecoversWithinASecond()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Lose = h.Hardware.Sessions.Count == 0 ? 1 : 0;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "submitted");
        var lost = h.Hardware.Last;
        Assert.Equal(1, lost.InFlight);

        // No further input: the watchdog alone finds it. The clock moves once, so only once the deadline is armed.
        await Eventually(() => h.Time.HasTimerDueAt(VideoStream.StallTimeout), "the watchdog's deadline is armed");
        h.Time.Advance(VideoStream.StallTimeout);
        await Eventually(() => lost.Aborted && h.Refreshes > 0, "the stall is found at one second, a fresh frame asked for");

        h.Frame(); // The frame the refresh asked for.
        await Eventually(() => h.Stream.Queued == 1, "the new session's keyframe");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(2, h.Hardware.Sessions.Count);
    }

    [Fact]
    public async Task WedgedAbort_DoesNotBlockTheNextSession()
    {
        using var h = new Harness();
        h.Hardware.Configure = s =>
        {
            if (h.Hardware.Sessions.Count != 0)
                return;
            s.Lose = 1;
            s.Wedged = true;
        };
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "submitted");
        var wedged = h.Hardware.Last;
        // The clock moves once, so only once the loop has armed its deadline: a move before that arms it a second late.
        await Eventually(() => h.Time.HasTimerDueAt(VideoStream.StallTimeout), "the watchdog's deadline is armed");
        h.Time.Advance(VideoStream.StallTimeout);
        await Eventually(() => wedged.Aborted, "aborted");

        await Eventually(() => h.Backends.Quarantines(h.Hardware) == 1 && h.Refreshes == 1, "the teardown is given up on at the bound", h.Time);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a new session encodes beside the wedged one");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
    }

    [Fact]
    public async Task AFailedStartsWedgedTeardown_DoesntHoldTheFallback_AndIsQuarantinedAtTheBound()
    {
        // A start that fails after creating its session hands the teardown back; it never ends.
        var wedged = new TaskCompletionSource();
        var hardware = new FakeH264Backend("Hardware") { Refuse = _ => H264StartProblem.Fault, RefusalTeardown = wedged.Task };
        using var h = new Harness(software: true, hardware: hardware);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "software takes the frame at once");
        Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);
        Assert.Equal(0, h.Backends.Quarantines(hardware));

        await Eventually(() => h.Backends.Quarantines(hardware) == 1, "the teardown is counted as quarantined at the bound", h.Time);
        wedged.SetResult();
    }

    [Fact]
    public async Task AStartsReleasedTeardown_IsQuarantinedAtTheBound_WhileItsSessionRuns()
    {
        // A start that let a session go before the one it returns (a retry in another pixel format) hands that teardown
        // back with the session. It never ends: it is counted at the bound, not once the session it left behind ends.
        var wedged = new TaskCompletionSource();
        using var h = new Harness();
        h.Hardware.Configure = s => s.Released = [wedged.Task];
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the session encodes at once");
        Assert.Equal("hardware", Assert.Single(h.Take()).Header.Encoder);
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));

        await Eventually(() => h.Backends.Quarantines(h.Hardware) == 1, "the released teardown is counted as quarantined at the bound", h.Time);
        Assert.False(h.Hardware.Last.Aborted);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the session still encodes");
        Assert.Single(h.Hardware.Sessions);
        wedged.SetResult();
    }

    [Fact]
    public async Task ARefusedStartsTeardown_WhoseWaitGivesTheBackendUpForTheRun_StartsNoSessionOfItAfterTheWait()
    {
        // A backend that starts beside a wedged teardown (as VideoToolbox), two quarantines in already: its busy start
        // leaves a wedged teardown and software serves meanwhile. At the retry it passes the checks, then waits for that
        // teardown, whose bound is the third quarantine: given up for the run, it starts nothing after the wait.
        var wedged = new TaskCompletionSource();
        var refusals = 0;
        var hardware = new FakeH264Backend("Hardware")
        {
            Refuse = _ => Interlocked.Increment(ref refusals) == 1 ? H264StartProblem.Busy : null,
            RefusalTeardown = wedged.Task,
        };
        // Software's first session's teardown is a gate the test ends, its continuations inline: ended from a pool thread
        // (no synchronization context), it runs the loop on that thread up to the loop's next wait before returning.
        var softwareTeardown = new TaskCompletionSource();
        var replacementStarter = 0;
        using var h = new Harness(software: true, hardware: hardware);
        var software = h.Software!;
        software.Configure = s =>
        {
            if (software.Sessions.Count == 0)
                s.Teardown = softwareTeardown.Task;
            else
                Volatile.Write(ref replacementStarter, Environment.CurrentManagedThreadId);
        };
        h.Backends.RecordQuarantine(hardware);
        h.Backends.RecordQuarantine(hardware);
        try
        {
            h.Frame();
            await Eventually(() => h.Stream.Queued == 1, "software takes the frame while hardware backs off");
            Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);

            // The backoff over, within the teardown's bound: software's session is retired for the better backend, the
            // loop held in that retirement (its bound's timer armed). Ending the retirement from a pool thread runs the loop
            // there, through hardware's checks (not given up: the clock is short of the bound), into its wait for hardware's
            // refused teardown: once back, the loop is in that wait.
            h.Time.Advance(VideoStream.BusyFirst);
            h.Frame();
            await Eventually(() => software.Last.Aborted && h.Time.HasTimerDueAt(VideoStream.BusyFirst + Quarantine.Bound),
                "software's session retiring for hardware");
            await Task.Run(softwareTeardown.SetResult, TestContext.Current.CancellationToken);
            Assert.False(h.Backends.IsGivenUp(hardware));
            Assert.Equal(1, hardware.Starts);

            // The bound, from a pool thread: the teardown is the third quarantine, hardware given up for the run during the
            // wait, and the loop, resumed on that thread, starts software's next session there, never hardware's. (One
            // started on another thread would be a loop that hadn't reached the wait: the given-up check before it, not this case.)
            await Task.Run(() =>
            {
                h.Time.Advance(Quarantine.Bound - VideoStream.BusyFirst);
                Assert.Equal(1, hardware.Starts);
                Assert.Equal(2, software.Sessions.Count);
                Assert.Equal(Environment.CurrentManagedThreadId, Volatile.Read(ref replacementStarter));
            }, TestContext.Current.CancellationToken);
            Assert.True(h.Backends.IsGivenUp(hardware));
            Assert.Equal(3, h.Backends.Quarantines(hardware));
            Assert.Empty(hardware.Sessions);

            // The replacement's first keyframe, taken before the frame that checks for churn.
            await Eventually(() => h.Stream.Queued >= 1, "software's new session encodes its first keyframe");
            var first = Assert.Single(h.Take());
            Assert.True(first.Header.Keyframe);
            Assert.Equal("software", first.Header.Encoder);

            // Given up, it is no better backend to retire software's session for: the next frame goes to that session.
            h.Time.Advance(Interval);
            h.Frame();
            await Eventually(() => h.Stream.Queued >= 1, "software's session encodes the next frame");
            Assert.Equal(2, software.Sessions.Count);
            Assert.False(Assert.Single(h.Take()).Header.Keyframe);
        }
        finally
        {
            softwareTeardown.TrySetResult();
            wedged.SetResult();
        }
    }

    [Fact]
    public async Task AWaitForATeardown_ThatOutlastsAnEarlierBackendsBackoff_TriesThatBackendAfterIt_AndNeverEndsTheStream()
    {
        // A is busy until 1 s; B refused the first size and its teardown runs on; C serves. A new size at 0.1 s skips A,
        // waits for B's teardown until 1.5 s, and B and C refuse the new size: A, free by then, is tried after them.
        var busyOnce = 0;
        var a = new FakeH264Backend("A") { Refuse = _ => Interlocked.Increment(ref busyOnce) == 1 ? H264StartProblem.Busy : null };
        var teardown = new TaskCompletionSource();
        var b = new FakeH264Backend("B") { Refuse = _ => H264StartProblem.Unsupported, RefusalTeardown = teardown.Task };
        var c = new FakeH264Backend("C", H264Kind.Software) { Refuse = config => config.Width == 80 ? H264StartProblem.Unsupported : null };
        using var h = new Harness(backends: new H264Backends([a, b, c], NullLogger.Instance), hardware: a);
        try
        {
            h.Frame();
            await Eventually(() => h.Stream.Queued == 1, "C takes the frame");
            Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);

            h.Time.Advance(TimeSpan.FromMilliseconds(100));
            h.Frame(80, 48);
            // The selection read its time (0.1 s) before it retired C; the time it reads after B's wait is 1.5 s.
            await Eventually(() => c.Last.Aborted, "the new size retires C's session");
            h.Time.Advance(TimeSpan.FromMilliseconds(1400));
            teardown.SetResult();

            await Eventually(() => a.Sessions.Count == 1, "A starts at the new size");
            Assert.Equal(80, a.Last.Config.Width);
            Assert.True(b.Starts >= 2);
            Assert.True(h.Backends.IsUnsupported(c, 80, 48));
            h.Frame(80, 48);
            await Eventually(() => h.Stream.Queued >= 1, "A's keyframe");
            var frame = h.Take()[0];
            Assert.True(frame.Header.Keyframe);
            Assert.Equal(80, frame.Header.PixelWidth);
            Assert.Equal("hardware", frame.Header.Encoder);
            Assert.False(h.Stream.Completion.IsCompleted);
        }
        finally
        {
            teardown.TrySetResult();
        }
    }

    [Fact]
    public async Task ThreeQuarantines_GiveTheBackendUp_ForTheRun()
    {
        var hardware = new FakeH264Backend("Hardware");
        var backends = new H264Backends([hardware], NullLogger.Instance);
        hardware.Configure = s =>
        {
            s.Hold = true;
            s.Wedged = true;
        };

        for (var i = 1; i <= 3; i++)
        {
            using var h = new Harness(backends: backends, hardware: hardware);
            h.Frame();
            var n = i;
            await Eventually(() => hardware.Sessions.Count == n && hardware.Last.Submitted.Count == 1, "submitted");
            h.Stream.Dispose();
            await Eventually(() => h.Stream.Completion.IsCompleted, "the stream ends within the bound", h.Time);
            Assert.Equal(n, backends.Quarantines(hardware));
        }

        Assert.True(backends.IsGivenUp(hardware));
        using var later = new Harness(backends: backends, hardware: hardware);
        Assert.Equal(VideoStream.Unavailable, later.Frame());
    }

    [Fact]
    public async Task Wedged_Close_ReturnsAtOnce_AndEndsWithinTheBound()
    {
        using var h = new Harness();
        h.Hardware.Configure = s =>
        {
            s.Hold = true;
            s.Wedged = true;
        };
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "submitted");

        var watch = Stopwatch.StartNew();
        h.Stream.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "Dispose returns at once");
        Assert.True(h.Hardware.Last.Aborted || !h.Stream.Completion.IsCompleted);
        await Eventually(() => h.Stream.Completion.IsCompleted, "ended at the bound", h.Time);
        Assert.True(h.Hardware.Last.Aborted);
        Assert.Equal(1, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task Wedged_Resize_StartsTheNextSessionWithinTheBound()
    {
        using var h = new Harness();
        h.Hardware.Configure = s =>
        {
            if (h.Hardware.Sessions.Count != 0)
                return;
            s.Hold = true;
            s.Wedged = true;
        };
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "submitted");

        h.Frame(80, 48);
        // The clock stays still until the loop has taken the new size: moved earlier, the held frame stalls first, and the
        // failure drops the new size's frame.
        await Eventually(() => h.Hardware.Sessions[0].Aborted, "the new size retires the old session");
        await Eventually(() => h.Hardware.Sessions.Count == 2, "the new size's session starts within the bound", h.Time);
        Assert.True(h.Hardware.Sessions[0].Aborted);
        Assert.Equal(1, h.Backends.Quarantines(h.Hardware));
        await Eventually(() => h.Stream.Queued == 1, "its keyframe");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(80, frame.Header.PixelWidth);
    }

    [Fact]
    public async Task Wedged_Resubscribe_StartsTheNextStreamAtOnce()
    {
        using var h = new Harness();
        h.Hardware.Configure = s =>
        {
            if (h.Hardware.Sessions.Count != 0)
                return;
            s.Hold = true;
            s.Wedged = true;
        };
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "submitted");

        var watch = Stopwatch.StartNew();
        h.Stream.Dispose();
        h.Replace(h.NewStream());
        h.Frame();
        // Without moving the clock: the new subscription doesn't wait for the old one's teardown.
        await Eventually(() => h.Stream.Queued == 1, "the new subscription's keyframe");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Equal(2, h.Hardware.Sessions.Count);
    }

    [Fact]
    public async Task RefusedFrameRate_IsAskedAgainAfterAnotherValue()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.RefusedFps = 20;
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a session");
        var session = h.Hardware.Last;

        h.Stream.SetFrameRate(20);
        await Eventually(() => session.FrameRates.Count == 1, "20 asked");
        h.Stream.SetFrameRate(20);
        await Settle();
        Assert.Equal([20], session.FrameRates);
        h.Stream.SetFrameRate(25);
        await Eventually(() => session.FrameRates.Count == 2, "25 asked");
        h.Stream.SetFrameRate(20);
        await Eventually(() => session.FrameRates.Count == 3, "20 asked again after another value");
        Assert.Equal([20, 25, 20], session.FrameRates);
    }

    // ---- one outcome per submission ----

    [Fact]
    public async Task AFailedRead_ReleasesItsSlot_AndLeavesNoPhantomWork()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Fail = h.Hardware.Sessions.Count == 0 ? 1 : 0;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted && h.Refreshes == 1, "the failed read is a failure");
        Assert.Equal(0, h.Stream.Occupied);
        Assert.Equal(0, h.Hardware.Sessions[0].InFlight);

        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the next session encodes");
        h.Take();
        // Nothing in flight: the watchdog finds no stall however long the panel stays still.
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await Settle();
        Assert.Equal(2, h.Hardware.Sessions.Count);
        Assert.False(h.Hardware.Last.Aborted);
    }

    // ---- the failure policy ----

    [Fact]
    public async Task ThreeFailuresInThirtySeconds_MoveTheStreamToSoftware()
    {
        using var h = new Harness(software: true);
        h.Hardware.Configure = s => s.Fail = 1;
        for (var i = 1; i <= 3; i++)
        {
            h.Frame();
            var n = i;
            await Eventually(() => h.Hardware.Sessions.Count == n && h.Hardware.Last.Aborted && h.Refreshes == n, $"failure {n}");
        }

        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "software encodes");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe);
        Assert.Equal("software", frame.Header.Encoder);
        Assert.Equal(3, h.Hardware.Sessions.Count);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
    }

    [Fact]
    public async Task NoBackendLeft_FramesArePng_EncoderFailed_UntilTheSubscriptionEnds()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Fail = 1;
        for (var i = 1; i <= 3; i++)
        {
            h.Frame();
            var n = i;
            await Eventually(() => h.Hardware.Sessions.Count == n && h.Hardware.Last.Aborted && h.Refreshes == n, $"failure {n}");
        }

        await Eventually(() => h.Frame() == VideoStream.Failed, "PNG with encoder.failed");
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(VideoStream.Failed, h.Frame());

        // A new subscription tries again.
        h.Hardware.Configure = _ => { };
        h.Stream.Dispose();
        h.Replace(h.NewStream());
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "hardware again");
    }

    [Fact]
    public async Task FaultyStartsAtThreeSizes_GiveTheBackendUpForTheRun_LaterStreamsAnswerUnavailable()
    {
        var hardware = new FakeH264Backend("Hardware") { Refuse = _ => H264StartProblem.Fault };
        var backends = new H264Backends([hardware], NullLogger.Instance);
        for (var i = 0; i < 3; i++)
        {
            using var h = new Harness(backends: backends, hardware: hardware);
            var n = i + 1;
            h.Frame(W + i * 2, H);
            await Eventually(() => hardware.Starts == n, "a faulty start");
            await Eventually(() => h.Frame(W + i * 2, H) is not null, "PNG once the start failed");
        }

        Assert.True(backends.IsGivenUp(hardware));
        using var later = new Harness(backends: backends, hardware: hardware);
        Assert.Equal(VideoStream.Unavailable, later.Frame());
    }

    [Fact]
    public async Task ARefusedAvccRewrite_GivesTheBackendUp_ForTheStream()
    {
        using var h = new Harness(software: true);
        h.Hardware.Configure = s => s.Avcc = FakeH264Backend.MainAvcc;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted && h.Refreshes == 1, "the avcC is refused");
        Assert.Empty(h.Take());

        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "software takes over at once");
        Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);
        Assert.Single(h.Hardware.Sessions);
    }

    [Theory]
    [InlineData("01420028FFE000")] // no parameter sets
    [InlineData("01420028FFE1000E27420028AB403203CD350101010201000168")] // a PPS that is only its NAL header
    public async Task AnAvccADecoderCantBeConfiguredWith_GivesTheBackendUp_ForTheStream(string hex)
    {
        // A keyframe's description no decoder can be configured with is never sent; the next backend is tried.
        using var h = new Harness(software: true);
        h.Hardware.Configure = s => s.Avcc = Convert.FromHexString(hex);
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted && h.Refreshes == 1, "the avcC is refused");
        Assert.Empty(h.Take());

        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "software takes over at once");
        Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);
        Assert.Single(h.Hardware.Sessions);
    }

    [Fact]
    public async Task AKeyframeThatSaysAnotherColour_GivesTheBackendUp_ForTheStream()
    {
        // The stream's colour is BT.709 limited range: a keyframe whose SPS says BT.601's matrix is never sent.
        using var h = new Harness(software: true);
        var avcc = FakeH264Backend.BaselineAvcc.ToArray();
        avcc[20] = 6; // matrix_coefficients
        h.Hardware.Configure = s => s.Avcc = avcc;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted && h.Refreshes == 1, "the avcC is refused");
        Assert.Empty(h.Take());

        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "software takes over at once");
        Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);
        Assert.Single(h.Hardware.Sessions);
    }

    // ---- failure classes ----

    [Fact]
    public async Task AnUnsupportedSize_IsCachedNotCounted_AndVideoResumesWithAKeyframe()
    {
        using var h = new Harness();
        h.Hardware.Refuse = c => c.Width == 200 ? H264StartProblem.Unsupported : null;
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "hardware");
        Assert.Equal("hardware", Assert.Single(h.Take()).Header.Encoder);

        h.Frame(200, H);
        await Eventually(() => h.Refreshes > 0, "the unsupported start asks a fresh frame");
        Assert.Equal(VideoStream.Size, h.Frame(200, H));
        Assert.Equal(VideoStream.Size, h.Frame(200, H));

        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "hardware again");
        var back = Assert.Single(h.Take());
        Assert.True(back.Header.Keyframe);
        Assert.Equal(W, back.Header.PixelWidth);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));

        // The run remembers the size, not a fault: another stream at the small size uses hardware.
        using var second = new Harness(backends: h.Backends, hardware: h.Hardware);
        Assert.Null(second.Frame());
        await Eventually(() => second.Stream.Queued == 1, "hardware in the second stream");
        Assert.Equal("hardware", Assert.Single(second.Take()).Header.Encoder);
        Assert.True(h.Backends.IsUnsupported(h.Hardware, 200, H));
    }

    [Fact]
    public async Task AnUnsupportedSize_GoesToTheNextBackend()
    {
        using var h = new Harness(software: true);
        h.Hardware.Refuse = c => c.Width == 200 ? H264StartProblem.Unsupported : null;
        h.Frame(200, H);
        await Eventually(() => h.Stream.Queued == 1, "software takes the size");
        Assert.Equal("software", Assert.Single(h.Take()).Header.Encoder);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "hardware takes the small size");
        Assert.Equal("hardware", Assert.Single(h.Take()).Header.Encoder);
    }

    [Fact]
    public async Task ABusyStartTwice_ThenFree_ResumesAfterTheBackoff_NothingCounted()
    {
        using var h = new Harness();
        var busy = 2;
        h.Hardware.Refuse = _ => Interlocked.Decrement(ref busy) >= 0 ? H264StartProblem.Busy : null;

        h.Frame();
        await Eventually(() => h.Hardware.Starts == 1 && h.Refreshes == 1, "busy once");
        Assert.Equal(VideoStream.Failed, h.Frame());
        h.Time.Advance(VideoStream.BusyFirst);
        await Eventually(() => h.Refreshes == 2, "the backoff ends");
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 2 && h.Refreshes >= 3, "busy twice");
        Assert.Equal(3, h.Refreshes);
        Assert.Equal(VideoStream.Failed, h.Frame());
        h.Time.Advance(VideoStream.BusyFirst);
        await Settle();
        Assert.Equal(VideoStream.Failed, h.Frame()); // The backoff doubled.
        h.Time.Advance(VideoStream.BusyFirst);
        await Eventually(() => h.Refreshes >= 4, "the second backoff ends");
        Assert.Equal(4, h.Refreshes);
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "video resumes");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
        Assert.Equal(3, h.Hardware.Starts);
    }

    [Fact]
    public async Task ABusyProbe_IsTriedAgainAfterTheBackoff_NotCachedAsUnavailable()
    {
        // The probe's own session finds no encoder free: transient, so the backoff applies, as for a busy start.
        var answers = new System.Collections.Concurrent.ConcurrentQueue<H264Probe>([H264Probe.Busy]);
        using var h = new Harness(hardware: new FakeH264Backend("Hardware")
        {
            ProbeAnswer = () => answers.TryDequeue(out var answer) ? answer : H264Probe.Available,
        });
        h.Frame();
        await Eventually(() => h.Hardware.Probes == 1 && h.Refreshes == 1, "the busy probe asks a fresh frame");
        Assert.False(h.Backends.NoneLeft);
        Assert.Equal(VideoStream.Failed, h.Frame());

        h.Time.Advance(VideoStream.BusyFirst);
        await Eventually(() => h.Refreshes == 2, "the backoff ends");
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "video, after a second probe");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Equal(2, h.Hardware.Probes);

        // Available is cached for the run: a later stream doesn't probe again.
        using var later = new Harness(backends: h.Backends, hardware: h.Hardware);
        Assert.Null(later.Frame());
        await Eventually(() => later.Stream.Queued == 1, "video in a later stream");
        Assert.Equal(2, h.Hardware.Probes);
    }

    // ---- the size rule ----

    [Fact]
    public async Task AFrameLargerThanTheVideoSize_IsPng_ThenVideoResumesWithAKeyframe()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a video frame");
        h.Frame();
        await Eventually(() => h.Stream.Queued == 2, "two video frames");
        h.Take();

        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 1, 16));
        Assert.Equal(VideoStream.Size, h.Frame(4096, 2305));
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "video again");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe);
        // The session was retired while the frames were PNG: video resumes in another.
        Assert.Equal(2, h.Hardware.Sessions.Count);
    }

    [Fact]
    public async Task LeavingVideo_RetiresTheSession_SoPngFramesHoldNoEncoder()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "video");
        h.Take();
        var first = h.Hardware.Last;

        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 1, 16));
        await Eventually(() => first.Aborted, "the session is retired while the frames are PNG");
        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 1, 16));
        await Settle();
        Assert.Single(h.Hardware.Sessions);

        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "video again, in a new session");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Equal(2, h.Hardware.Sessions.Count);
        Assert.False(h.Hardware.Last.Aborted);
    }

    [Fact]
    public async Task VideoQueuedBeforeAPngFrame_IsNotSentAfterIt()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "queued");
        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 2, 16));
        Assert.Empty(h.Take());
    }

    // ---- keyframe requests ----

    [Fact]
    public async Task KeyframeRequests_OnAStillPanel_AreEachAnswered_FourInARow()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "first keyframe");
        h.Take();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a delta");
        Assert.False(Assert.Single(h.Take()).Header.Keyframe);

        for (var i = 1; i <= 4; i++)
        {
            var before = h.Refreshes;
            h.Stream.RequestKeyframe();
            Assert.Equal(before + 1, h.Refreshes);
            h.Frame(); // The fresh frame the refresh asked for.
            await Eventually(() => h.Stream.Queued == 1, $"keyframe {i}");
            Assert.True(Assert.Single(h.Take()).Header.Keyframe, $"request {i} answered");
        }

        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a delta after");
        Assert.False(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Single(h.Hardware.Sessions);
    }

    [Fact]
    public async Task ADroppedKeyframe_StaysOwed_AndAFreshFrameIsAskedFor()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Drop = 1;
        h.Frame();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1 && h.Refreshes == 1,
            "the keyframe is dropped and a fresh frame asked for");
        Assert.Equal(0, h.Stream.Occupied);

        h.Frame(); // The fresh frame.
        await Eventually(() => h.Stream.Queued == 1, "the next frame is encoded");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe, "no delta is sent before a keyframe of its generation");
        Assert.Equal([true, true], h.Hardware.Last.Submitted.Select(s => s.Force));
        Assert.Single(h.Hardware.Sessions);
    }

    [Fact]
    public async Task AKeyframeDroppedAfterPngFrames_StaysOwed()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "video");
        h.Take();
        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 1, 16));

        // Whichever session takes the frames after the PNG one drops its first.
        h.Hardware.Configure = s => s.Drop = 1;
        Interlocked.Exchange(ref h.Hardware.Last.Drop, 1);
        var refreshes = h.Refreshes;
        h.Frame();
        await Eventually(() => h.Refreshes > refreshes && h.Hardware.Last.Submitted.Count > 0, "the keyframe is dropped and a fresh frame asked for");
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the next frame is encoded");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.All(h.Hardware.Last.Submitted.TakeLast(2), s => Assert.True(s.Force));
    }

    // ---- headers ----

    [Fact]
    public async Task AKeyframe_CarriesTheRewrittenAvcc_ItsCodec_AndTheEncoder()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a keyframe");
        var header = Assert.Single(h.Take()).Header;
        Assert.Equal("h264", header.Encoding);
        Assert.True(header.Keyframe);
        Assert.Equal("hardware", header.Encoder);
        var description = Convert.FromBase64String(header.Description!);
        Assert.Equal(H264Sps.RewriteForStream(FakeH264Backend.BaselineAvcc, out var problem), description);
        Assert.Null(problem);
        Assert.Equal($"avc1.{description[1]:X2}{description[2]:X2}{description[3]:X2}", header.Codec);
        Assert.NotNull(header.EncodeMs);
    }

    [Fact]
    public async Task PixelsPairWithTheirHeaders_UnderEveryInterleaving()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var random = new Random(seed);
            using var h = new Harness();
            h.Hardware.Configure = s => s.Hold = true;
            for (var step = 0; step < 60; step++)
            {
                switch (random.Next(4))
                {
                    case 0:
                    case 1:
                        h.Frame();
                        break;
                    case 2:
                        if (h.Hardware.Sessions.Count > 0)
                            h.Hardware.Last.Release(random.Next(1, 3));
                        break;
                    default:
                        h.Take();
                        break;
                }

                if (random.Next(3) == 0)
                    await Task.Delay(1, TestContext.Current.CancellationToken);
            }

            if (h.Hardware.Sessions.Count > 0)
                h.Hardware.Last.Release();
            await Settle();
            h.Take();
            foreach (var frame in h.Sent)
                Assert.Equal((byte)frame.Header.Tick, PixelTick(frame));
            Assert.Equal(h.Sent.Select(f => f.Header.Tick).Order(), h.Sent.Select(f => f.Header.Tick));
        }
    }

    // ---- waiting episodes: images while a video encoder is chosen, started or retired ----

    /// <summary>A start that waits for <see cref="Open"/> (the test's clock moves meanwhile).</summary>
    private sealed class Gate : IDisposable
    {
        private readonly ManualResetEventSlim _open = new();

        public H264StartProblem? Wait()
        {
            _open.Wait(TimeSpan.FromSeconds(30));
            return null;
        }

        public void Open() => _open.Set();

        public void Dispose() => _open.Set();
    }

    /// <summary>A probe that answers once <paramref name="done"/> is set (the test's clock moves meanwhile).</summary>
    private static H264Probe Probe(ManualResetEventSlim done, H264Probe answer)
    {
        done.Wait(TimeSpan.FromSeconds(30));
        return answer;
    }

    /// <summary>The PNG frames a stream sent from the first one on are every frame it routed, until <paramref name="lastTick"/>.</summary>
    private static void AssertPngThrough(Harness h, long lastTick)
    {
        var first = h.Pngs[0].Tick;
        Assert.Equal(Enumerable.Range((int)first, (int)(lastTick - first + 1)).Select(t => (long)t), h.Pngs.Select(p => p.Tick));
        Assert.All(h.Pngs, p => Assert.Equal(VideoStream.Failed, p.Fallback));
    }

    /// <summary>The cutover: the first video frame is a keyframe with its codec, of the last PNG frame's tick or later, and nothing follows it as PNG.</summary>
    private static void AssertCutOver(Harness h)
    {
        var key = h.Sent[0];
        Assert.True(key.Header.Keyframe, "the first video frame is a keyframe");
        Assert.NotNull(key.Header.Codec);
        var lastPng = h.Pngs.Count == 0 ? long.MinValue : h.Pngs.Max(p => p.Tick);
        Assert.True(key.Header.Tick >= lastPng, $"keyframe {key.Header.Tick} after PNG frame {lastPng}");
        Assert.All(h.Sent.Skip(1), f => Assert.True(f.Header.Tick > lastPng, $"video frame {f.Header.Tick} after PNG frame {lastPng}"));
        Assert.Equal(h.Sent.Select(f => f.Header.Tick).Order(), h.Sent.Select(f => f.Header.Tick));
        Assert.All(h.Sent, f => Assert.Equal((byte)f.Header.Tick, PixelTick(f)));
    }

    [Fact]
    public async Task TimeToTheFirstFallbackFrame_ThroughTwoSlowProbes_PngFramesUntilTheSecondsKeyframe()
    {
        using var firstProbe = new ManualResetEventSlim();
        using var secondProbe = new ManualResetEventSlim();
        var first = new FakeH264Backend("First") { ProbeAnswer = () => Probe(firstProbe, H264Probe.Unavailable) };
        var second = new FakeH264Backend("Second") { ProbeAnswer = () => Probe(secondProbe, H264Probe.Available) };
        using var h = new Harness(backends: new H264Backends([first, second], NullLogger.Instance), hardware: second);
        try
        {
            Assert.Null(h.Frame());
            await Eventually(() => h.Stream.Waiting && first.Probes == 1, "the first video frame waits for the first probe");
            await h.Play(TimeSpan.FromSeconds(3));
            firstProbe.Set();
            await Eventually(() => second.Probes == 1, "the first answers Unavailable, the second probes");
            await h.Play(TimeSpan.FromSeconds(8));

            var png = h.Pngs[0];
            Assert.InRange(png.At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);
            AssertPngThrough(h, h.Tick);
            Assert.Empty(h.Sent);

            secondProbe.Set();
            await Eventually(() => second.Sessions.Count == 1 && !h.Stream.Waiting, "the second's session cuts over");
            await h.Play(TimeSpan.FromSeconds(8.5));
            AssertCutOver(h);
            Assert.Equal(h.Pngs[^1].Tick, h.Sent[0].Header.Tick); // The newest picture the client saw.
            AssertPngThrough(h, h.Sent[0].Header.Tick);
            Assert.True(h.Sent.Count > 5);
        }
        finally
        {
            firstProbe.Set();
            secondProbe.Set();
        }
    }

    [Fact]
    public async Task ARetirementOfTwoSeconds_PngFramesFromOneSecond_ThenAKeyframeAtTheNewSize()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Wedged = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();
        h.Sent.Clear();
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "a delta, left in the queue");
        var old = h.Hardware.Last;

        Assert.Null(h.Frame(80, 48));
        await Eventually(() => old.Aborted && h.Stream.Waiting, "the new size retires the session");
        // The send loop takes nothing until the PNG frames have begun.
        await h.Play(TimeSpan.FromSeconds(1.95), width: 80, height: 48, take: false);
        old.Unwedge();
        await Eventually(() => h.Hardware.Sessions.Count == 2 && !h.Stream.Waiting, "the new size's session cuts over");
        await h.Play(TimeSpan.FromSeconds(2.2), width: 80, height: 48);

        Assert.InRange(h.Pngs[0].At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);
        // The delta queued before the PNG frames is older video: never sent after them.
        Assert.DoesNotContain(h.Sent, f => f.Header.Tick == 2);
        AssertCutOver(h);
        Assert.Equal(80, h.Sent[0].Header.PixelWidth);
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task ANormalStart_AndASizeChange_SendNoPngFrame()
    {
        using var firstStart = new Gate();
        using var resize = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => h.Hardware.Starts == 1 ? firstStart.Wait() : resize.Wait();

        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(0.6));
        firstStart.Open();
        await Eventually(() => !h.Stream.Waiting, "the cutover");
        await h.Play(TimeSpan.FromSeconds(0.8));

        Assert.Null(h.Frame(80, 48));
        await Eventually(() => h.Hardware.Starts == 2 && h.Stream.Waiting, "the new size's start");
        await h.Play(TimeSpan.FromSeconds(1.13), width: 80, height: 48);
        resize.Open();
        await Eventually(() => !h.Stream.Waiting, "the cutover at the new size");
        await h.Play(TimeSpan.FromSeconds(1.4), width: 80, height: 48);

        Assert.Empty(h.Pngs);
        Assert.Equal(1, h.Sent[0].Header.Tick); // The frame that waited is the candidate.
        AssertCutOver(h);
        var resized = h.Sent.First(f => f.Header.PixelWidth == 80);
        Assert.True(resized.Header.Keyframe);
    }

    [Fact]
    public async Task TheSessionBeingStarted_IsNotRetired_WhenTheEpisodesPngFramesBegin()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.5));
        Assert.NotEmpty(h.Pngs);
        start.Open();
        await Eventually(() => !h.Stream.Waiting, "the cutover");
        await h.Play(TimeSpan.FromSeconds(1.7));

        var session = Assert.Single(h.Hardware.Sessions);
        Assert.False(session.Aborted, "the started session is used");
        Assert.Equal(1, h.Hardware.Starts);
        AssertCutOver(h);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeltaFirstInAGeneration_IsNeverSent_TheSessionFailsOnce_ThenVideoResumesWithAKeyframe(bool changesLive)
    {
        using var h = new Harness(hardware: new FakeH264Backend("Hardware") { ChangesLive = changesLive });
        h.Hardware.Configure = s => s.FirstDelta = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted && h.Refreshes == 1, "the delta fails the session");
        Assert.Empty(h.Take());
        Assert.Equal(0, h.Stream.Occupied);
        Assert.True(h.Stream.Waiting, "the episode goes on");

        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the next session's keyframe");
        var frame = Assert.Single(h.Take());
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(2, frame.Header.Tick);
        Assert.Equal(2, h.Hardware.Sessions.Count);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
        Assert.False(h.Stream.Waiting);
    }

    [Fact]
    public async Task AKeyframeWhoseAvccIsRefused_InAnEpisode_SendsNothingOfItsGeneration_AndGivesTheBackendUp()
    {
        using var start = new Gate();
        using var h = new Harness(software: true);
        h.Hardware.Refuse = _ => start.Wait();
        h.Hardware.Configure = s => s.Avcc = FakeH264Backend.MainAvcc;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.1));
        start.Open();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted, "the avcC is refused");
        await h.Play(TimeSpan.FromSeconds(1.5));

        Assert.NotEmpty(h.Sent);
        Assert.All(h.Sent, f => Assert.Equal("software", f.Header.Encoder ?? "software"));
        Assert.Equal("software", h.Sent[0].Header.Encoder);
        AssertCutOver(h);
        Assert.Single(h.Hardware.Sessions);
        Assert.False(h.Backends.IsGivenUp(h.Hardware), "given up for the stream only");
    }

    [Fact]
    public async Task RecoveryAfterTheGrace_AHealthyColdStartOf1200ms_PngFramesThenVideo_NothingCounted()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.2));
        start.Open();
        await Eventually(() => !h.Stream.Waiting, "the cutover");
        await h.Play(TimeSpan.FromSeconds(1.5));

        Assert.InRange(h.Pngs[0].At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);
        AssertPngThrough(h, h.Sent[0].Header.Tick);
        AssertCutOver(h);
        var session = Assert.Single(h.Hardware.Sessions);
        Assert.False(session.Aborted, "nothing was retired");
        Assert.Equal(1, h.Hardware.Starts);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task ADelayedFirstKeyframe_PngFramesUntilACandidateIsIn_ThenItsKeyframeFirst()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        h.Hardware.Configure = s => s.Hold = true;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.1));
        var pngsAtStart = h.Pngs.Count;
        start.Open();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "the candidate is submitted");
        var session = h.Hardware.Last;
        await h.Play(TimeSpan.FromSeconds(1.3));
        Assert.Empty(h.Sent);
        Assert.True(h.Stream.Waiting, "Start returning doesn't end the episode");
        session.Release();
        await Eventually(() => !h.Stream.Waiting, "the candidate's keyframe cuts over");
        await h.Play(TimeSpan.FromSeconds(1.6), () => session.Release());
        session.Release();
        await Settle();
        h.Take();

        Assert.True(pngsAtStart > 0);
        Assert.Equal(h.Pngs[pngsAtStart - 1].Tick, h.Sent[0].Header.Tick);
        AssertCutOver(h);
        Assert.True(h.Sent.Count > 5);
    }

    [Fact]
    public async Task AFirstKeyframeOf400ms_GoesStaleAt250ms_AndTheSecondCandidateCutsOverWithinItsWait()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        h.Hardware.Configure = s => s.Hold = true;

        // The encoder holds each forced frame 400 ms and gives the others at once, in order.
        var seen = new Dictionary<long, TimeSpan>();
        void Encode()
        {
            if (h.Hardware.Sessions.Count == 0)
                return;
            var session = h.Hardware.Last;
            while (true)
            {
                var submitted = session.Submitted;
                foreach (var (id, _) in submitted)
                    seen.TryAdd(id, h.Now);
                var held = session.Held;
                if (held == 0)
                    return;
                var (head, forced) = submitted[submitted.Count - held];
                if (h.Now < seen[head] + (forced ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero))
                    return;
                session.Release(1);
            }
        }

        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.1));
        start.Open();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "the first candidate");
        Encode();
        var firstAt = seen[0];
        var firstCandidate = h.Tick; // At most this: the newest frame offered when it was taken.
        await h.Play(TimeSpan.FromSeconds(3), Encode);

        Assert.False(h.Stream.Waiting, "the cutover");
        // The first candidate went stale at its 250 ms: a PNG frame went out then.
        Assert.Contains(h.Pngs, p => p.At >= firstAt + VideoStream.CandidateWait);
        Assert.True(h.Sent[0].Header.Tick > firstCandidate, "the second candidate's keyframe is the cutover");
        AssertCutOver(h);
        // The second candidate waited 500 ms: its 400 ms keyframe made it.
        Assert.True(h.Pngs[^1].At < firstAt + VideoStream.CandidateWait + TimeSpan.FromMilliseconds(400) + Interval * 3,
            $"the last PNG frame at {h.Pngs[^1].At}");
    }

    [Fact]
    public async Task ADroppedFirstKeyframe_PngFramesGoOn_AFreshFrameIsAskedFor_AndTheNextCandidateCutsOver()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        h.Hardware.Configure = s => s.Drop = 1;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.1));
        var refreshes = h.Refreshes;
        start.Open();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count >= 1 && h.Refreshes > refreshes,
            "the candidate is dropped and a fresh frame asked for");
        var dropped = h.Hardware.Last.Submitted[0];
        Assert.True(h.Stream.Waiting);
        await h.Play(TimeSpan.FromSeconds(1.4));

        Assert.False(h.Stream.Waiting);
        Assert.Contains(h.Pngs, p => p.At > TimeSpan.FromSeconds(1.1));
        AssertCutOver(h);
        Assert.True(dropped.Force);
        Assert.True(h.Hardware.Last.Submitted[1].Force, "the next candidate is forced too");
        Assert.Single(h.Hardware.Sessions);
    }

    [Fact]
    public async Task FlappingWaits_DontResetTheGrace_AndPngFramesGoOnUntilAWorkingSessionsKeyframe()
    {
        using var secondStart = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => h.Hardware.Starts == 2 ? secondStart.Wait() : null;
        h.Hardware.Configure = s =>
        {
            var n = h.Hardware.Sessions.Count;
            s.Fail = n < 2 ? 1 : 0;
            s.Hold = n == 0;
            s.Wedged = n == 0;
        };

        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1 && h.Stream.Waiting, "the first session");
        var first = h.Hardware.Last;
        // Its first output fails at 0.5 s; its retirement takes 0.4 s; the second session's start 0.3 s, then its first output fails too.
        await h.Play(TimeSpan.FromSeconds(2), () =>
        {
            if (h.Now >= TimeSpan.FromSeconds(0.5))
                first.Release();
            if (h.Now >= TimeSpan.FromSeconds(0.9))
                first.Unwedge();
            if (h.Now >= TimeSpan.FromSeconds(1.2))
                secondStart.Open();
        });

        Assert.Equal(3, h.Hardware.Sessions.Count);
        Assert.False(h.Stream.Waiting);
        Assert.InRange(h.Pngs[0].At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);
        AssertPngThrough(h, h.Sent[0].Header.Tick);
        AssertCutOver(h);
        Assert.False(h.Backends.IsGivenUp(h.Hardware), "two failures, under the three that give it up");
    }

    [Fact]
    public async Task OneFrameOnly_TheTimersRefreshBringsAPngFrame_AndTheCandidateCutsOverWithNoFrameOffered()
    {
        using var probe = new ManualResetEventSlim();
        using var h = new Harness(hardware: new FakeH264Backend("Hardware")
        {
            ProbeAnswer = () => Probe(probe, H264Probe.Available),
        });
        try
        {
            // The render loop of a panel that doesn't change: a frame only when one is asked for.
            var answered = 0;
            void Answer()
            {
                while (h.Refreshes > answered)
                {
                    answered++;
                    h.Frame();
                }
            }

            Assert.Null(h.Frame());
            await Eventually(() => h.Stream.Waiting && h.Hardware.Probes == 1, "the selection waits");
            while (h.Now < TimeSpan.FromSeconds(8))
            {
                h.Time.Advance(Interval);
                Answer();
                await Task.Delay(2, TestContext.Current.CancellationToken);
            }

            var png = Assert.Single(h.Pngs);
            Assert.InRange(png.At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);
            probe.Set();
            await Eventually(() =>
            {
                Answer();
                return !h.Stream.Waiting && h.Stream.Queued == 1;
            }, "the candidate cuts over");
            var key = Assert.Single(h.Take());
            Assert.True(key.Header.Keyframe);
            Assert.Equal(png.Tick, key.Header.Tick);
            Assert.Equal(1 + answered, h.Tick);

            // The episode is over: its timer never asks again.
            var refreshes = h.Refreshes;
            h.Time.Advance(TimeSpan.FromSeconds(5));
            await Settle();
            Assert.Equal(refreshes, h.Refreshes);
        }
        finally
        {
            probe.Set();
        }

        // Nor does a disposed stream's.
        using var disposedProbe = new ManualResetEventSlim();
        using var d = new Harness(hardware: new FakeH264Backend("Hardware")
        {
            ProbeAnswer = () => Probe(disposedProbe, H264Probe.Available),
        });
        try
        {
            Assert.Null(d.Frame());
            await Eventually(() => d.Stream.Waiting && d.Hardware.Probes == 1, "the selection waits");
            d.Time.Advance(TimeSpan.FromSeconds(0.5));
            d.Stream.Dispose();
            d.Time.Advance(TimeSpan.FromSeconds(2));
            await Settle();
            Assert.Equal(0, d.Refreshes);
        }
        finally
        {
            disposedProbe.Set();
        }
    }

    [Fact]
    public async Task TickOrderAtTheCutover_AKeyframeHeldPastItsWait_IsNeverSent()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        h.Hardware.Configure = s => s.Hold = true;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.1));
        start.Open();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1, "the first candidate");
        var session = h.Hardware.Last;
        var held = h.Tick; // At most this: the newest frame offered when it was taken.

        // Held past its 250 ms: a PNG frame of a later tick goes out; then it comes back, and so does everything after.
        await h.Play(TimeSpan.FromSeconds(1.4));
        var stalePng = h.Pngs.Last(p => p.At <= TimeSpan.FromSeconds(1.4));
        Assert.True(stalePng.At > TimeSpan.FromSeconds(1.1) + VideoStream.CandidateWait - Interval);
        await h.Play(TimeSpan.FromSeconds(1.9), () => session.Release());
        session.Release();
        await Settle();
        h.Take();

        Assert.False(h.Stream.Waiting);
        Assert.DoesNotContain(h.Sent, f => f.Header.Tick <= held);
        AssertCutOver(h);
        Assert.True(h.Sent[0].Header.Tick >= stalePng.Tick);
    }

    [Fact]
    public async Task TickOrderAtTheCutover_AFrameOvertakenByPngFramesWhileItsSessionStarts_IsNeverTheCandidate()
    {
        using var firstStart = new Gate();
        using var secondStart = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => h.Hardware.Starts == 1 ? firstStart.Wait() : h.Hardware.Starts == 2 ? secondStart.Wait() : null;
        h.Hardware.Configure = s => s.Fail = h.Hardware.Sessions.Count == 0 ? 1 : 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Play(TimeSpan.FromSeconds(1.1));

        // The first session fails at once; the frame the loop takes next waits for the second start, while PNG frames of
        // later ticks go out (offered too, so the newest one is the candidate).
        firstStart.Open();
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Aborted, "the first session fails");
        await h.Play(TimeSpan.FromSeconds(1.2));
        await Eventually(() => h.Hardware.Starts == 2, "the second start");
        var waited = h.Tick;
        await h.Play(TimeSpan.FromSeconds(1.5));
        secondStart.Open();
        await Eventually(() => !h.Stream.Waiting, "the cutover");
        await h.Play(TimeSpan.FromSeconds(1.7));

        Assert.True(h.Pngs.Max(p => p.Tick) > waited);
        AssertCutOver(h);
        Assert.True(h.Sent[0].Header.Tick > waited, $"the candidate {h.Sent[0].Header.Tick} is newer than the frame that waited ({waited} or older)");
    }

    [Fact]
    public async Task ASkippedCandidate_OnAStillPanel_AFreshFrameIsAskedFor_AndTheNextCandidateCutsOver()
    {
        using var start = new Gate();
        using var h = new Harness();
        h.Hardware.Refuse = _ => start.Wait();
        h.Hardware.Configure = s => s.SkipSubmit = 1;
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Starts == 1 && h.Stream.Waiting, "the start");
        await h.Still(TimeSpan.FromSeconds(1.1));
        var png = Assert.Single(h.Pngs);
        Assert.InRange(png.At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);

        // The PNG frame's pixels are the candidate, and its submission is skipped (a buffer the encoder couldn't lock).
        start.Open();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return !h.Stream.Waiting && h.Sent.Count > 0;
        }, "a fresh frame is the next candidate and cuts over");

        Assert.Equal(2, h.Pngs.Count);
        Assert.Equal(h.Pngs[^1].Tick, h.Sent[0].Header.Tick);
        AssertCutOver(h);
        Assert.Single(h.Hardware.Sessions);
    }

    [Fact]
    public async Task AFirstCandidateFailingAtHalfASecond_WithA1900msTeardown_OnAStillPanel_SendsAPngFrameAtTheGrace()
    {
        using var h = new Harness();
        h.Hardware.Configure = s =>
        {
            var first = h.Hardware.Sessions.Count == 0;
            s.Hold = first;
            s.Fail = first ? 1 : 0;
            s.Wedged = first;
        };
        Assert.Null(h.Frame());
        await Eventually(() => h.Hardware.Sessions.Count == 1 && h.Hardware.Last.Submitted.Count == 1 && h.Stream.Waiting, "the first candidate");
        var first = h.Hardware.Last;

        // Its output fails at 0.5 s and its teardown takes 1.9 s; the panel doesn't change.
        await h.Still(TimeSpan.FromSeconds(0.5));
        first.Release();
        await Eventually(() => first.Aborted, "the failure retires the session");
        await h.Still(TimeSpan.FromSeconds(2.4));
        first.Unwedge();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return h.Hardware.Sessions.Count == 2 && !h.Stream.Waiting && h.Sent.Count > 0;
        }, "the next session cuts over");

        Assert.InRange(h.Pngs[0].At, VideoStream.PendingGrace, VideoStream.PendingGrace + Interval);
        AssertCutOver(h);
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task AFrameRateChange_OfABackendWithoutLiveChanges_WithA1900msTeardown_PngFramesFromTheGrace_ThenTheNewRatesKeyframe()
    {
        using var h = new Harness(hardware: new FakeH264Backend("Hardware") { ChangesLive = false });
        h.Hardware.Configure = s => s.Wedged = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();
        h.Sent.Clear();
        var old = h.Hardware.Last;

        h.Stream.SetFrameRate(15);
        await Eventually(() => old.Aborted, "the new rate retires the session");
        var at = h.Now;
        await h.Play(at + TimeSpan.FromSeconds(1.9), take: false);
        old.Unwedge();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return h.Hardware.Sessions.Count == 2 && !h.Stream.Waiting && h.Sent.Count > 0;
        }, "a session at the new rate cuts over");
        await h.Play(at + TimeSpan.FromSeconds(2.2));

        Assert.NotEmpty(h.Pngs);
        Assert.InRange(h.Pngs[0].At, at + VideoStream.PendingGrace, at + VideoStream.PendingGrace + Interval);
        AssertCutOver(h);
        Assert.Equal(15, h.Hardware.Last.Config.Fps);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task AKeyframeRequest_OfABackendWithoutLiveChanges_WithA1900msTeardown_PngFramesFromTheGrace_ThenTheNewSessionsKeyframe()
    {
        using var h = new Harness(hardware: new FakeH264Backend("Hardware") { ChangesLive = false });
        h.Hardware.Configure = s => s.Wedged = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();
        h.Sent.Clear();
        var old = h.Hardware.Last;

        h.Stream.RequestKeyframe();
        Assert.Null(h.Frame());
        await Eventually(() => old.Aborted, "the request retires the session");
        var at = h.Now;
        await h.Play(at + TimeSpan.FromSeconds(1.9), take: false);
        old.Unwedge();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return h.Hardware.Sessions.Count == 2 && !h.Stream.Waiting && h.Sent.Count > 0;
        }, "a new session's keyframe cuts over");
        await h.Play(at + TimeSpan.FromSeconds(2.2));

        Assert.NotEmpty(h.Pngs);
        Assert.InRange(h.Pngs[0].At, at + VideoStream.PendingGrace, at + VideoStream.PendingGrace + Interval);
        AssertCutOver(h);
        Assert.False(h.Backends.IsGivenUp(h.Hardware));
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task AFailureAfterTheCutover_WithA1900msTeardown_PngFramesFromTheGrace_ThenTheNextSessionsKeyframe()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Wedged = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();
        h.Sent.Clear();
        var old = h.Hardware.Last;

        old.Fail = 1;
        Assert.Null(h.Frame());
        await Eventually(() => old.Aborted, "the failure retires the session");
        var at = h.Now;
        await h.Play(at + TimeSpan.FromSeconds(1.9), take: false);
        old.Unwedge();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return h.Hardware.Sessions.Count == 2 && !h.Stream.Waiting && h.Sent.Count > 0;
        }, "the next session cuts over");
        await h.Play(at + TimeSpan.FromSeconds(2.2));

        Assert.NotEmpty(h.Pngs);
        Assert.InRange(h.Pngs[0].At, at + VideoStream.PendingGrace, at + VideoStream.PendingGrace + Interval);
        AssertCutOver(h);
        Assert.False(h.Backends.IsGivenUp(h.Hardware), "one failure");
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task LeavingVideo_WithA1900msTeardown_FramesBackAfter100ms_PngFramesFromTheGrace_ThenTheNextSessionsKeyframe()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Wedged = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();
        h.Sent.Clear();
        var old = h.Hardware.Last;

        // An oversized frame is PNG: the session is retired, and its teardown takes 1.9 s.
        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 1, H));
        await Eventually(() => old.Aborted, "leaving video retires the session");
        var at = h.Now;

        // Frames go to video again 0.1 s later, while the teardown runs: they wait for the next session from there.
        h.Time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Null(h.Frame());
        var resumed = h.Now;
        var waiting = h.Stream.Waiting;
        await h.Play(at + TimeSpan.FromSeconds(1.9), take: false);
        old.Unwedge();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return h.Hardware.Sessions.Count == 2 && !h.Stream.Waiting && h.Sent.Count > 0;
        }, "the next session cuts over");
        await h.Play(at + TimeSpan.FromSeconds(2.2));

        var pngs = h.Pngs.Where(p => p.Fallback == VideoStream.Failed).ToList();
        Assert.NotEmpty(pngs);
        Assert.InRange(pngs[0].At, resumed + VideoStream.PendingGrace, resumed + VideoStream.PendingGrace + Interval);
        Assert.True(waiting, "the frame that went back to video opened the episode");
        AssertCutOver(h);
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }

    [Fact]
    public async Task PngFramesDuringAFailuresRetirement_FramesBackBeforeItEnds_PngFramesFromTheGrace_ThenTheNextSessionsKeyframe()
    {
        using var h = new Harness();
        h.Hardware.Configure = s => s.Wedged = h.Hardware.Sessions.Count == 0;
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();
        h.Sent.Clear();
        var old = h.Hardware.Last;

        old.Fail = 1;
        Assert.Null(h.Frame());
        await Eventually(() => old.Aborted && h.Stream.Waiting, "the failure retires the session");
        var at = h.Now;

        // During its 1.9 s teardown an oversized frame is PNG for another reason, which ends the episode; frames go to
        // video again 0.1 s later, while the teardown still runs.
        h.Time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(VideoStream.Size, h.Frame(VideoStream.MaxSide + 1, H));
        Assert.False(h.Stream.Waiting);
        h.Time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Null(h.Frame());
        var resumed = h.Now;
        var waiting = h.Stream.Waiting;
        await h.Play(at + TimeSpan.FromSeconds(1.9), take: false);
        old.Unwedge();
        await Eventually(() =>
        {
            h.Answer();
            h.Take();
            return h.Hardware.Sessions.Count >= 2 && !h.Stream.Waiting && h.Sent.Count > 0;
        }, "the next session cuts over");
        await h.Play(at + TimeSpan.FromSeconds(2.2));

        var pngs = h.Pngs.Where(p => p.Fallback == VideoStream.Failed).ToList();
        Assert.NotEmpty(pngs);
        Assert.InRange(pngs[0].At, resumed + VideoStream.PendingGrace, resumed + VideoStream.PendingGrace + Interval);
        Assert.True(waiting, "the frame that went back to video opened the episode");
        AssertCutOver(h);
        Assert.Equal(0, h.Backends.Quarantines(h.Hardware));
    }
}
