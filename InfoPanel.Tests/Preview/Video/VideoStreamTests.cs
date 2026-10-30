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
        public List<(long Tick, string Fallback)> Pngs { get; } = [];
        public List<EncodedFrame> Sent { get; } = [];

        public VideoStream NewStream() => new(Backends, 30, () => { }, () => Interlocked.Increment(ref _refreshes), Skipped, NullLogger.Instance, Time);

        public void Replace(VideoStream stream) => Stream = stream;

        /// <summary>The next frame (its pixels all its tick's low byte): to the encoder, or recorded as PNG with its reason, which is returned.</summary>
        public string? Frame(int width = W, int height = H)
        {
            var tick = ++_tick;
            var fallback = Stream.Route(width, height);
            if (fallback != null)
            {
                Pngs.Add((tick, fallback));
                return fallback;
            }

            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)tick);
            Stream.Offer(pixels, Header(tick, width, height));
            return null;
        }

        public long Tick => _tick;

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
        await Eventually(() => h.Hardware.Sessions.Count == 2 && h.Stream.Queued >= 1, "the new session encodes");
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

        // No further input: the watchdog alone finds it.
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
}
