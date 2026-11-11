using System.Diagnostics;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// A real <see cref="VideoStream"/> over Media Foundation's backend (on <see cref="FakeMfCatalog"/>) as Windows' whole
/// chain, with no other backend: keyframe requests forced or answered by new sessions, a forced delta, a wedged
/// shutdown, an unsupported size, a busy begin, a busy start's probe that recovers, a refused start whose teardown is
/// wedged (nothing opened beside it), and a probe that finds nothing. The
/// stream runs on a clock the test moves; the backend's own bounds on a clock of their own, which these cases never move.
/// </summary>
public class MediaFoundationStreamTests
{
    private const int W = 64;
    private const int H = 48;
    private const string K = @"PCI\VEN_1002&DEV_1900&SUBSYS_00311F66&REV_C5\4&1234&0&0041";

    /// <summary>The run's chain: Media Foundation alone, over one catalog.</summary>
    private sealed class Chain : IDisposable
    {
        /// <param name="held">A log line that waits for the gate before it is recorded (<see cref="HeldLogger"/>).</param>
        /// <param name="reuse">False: every session shuts its own activation down (a refused re-use's path).</param>
        public Chain(bool deviceKey = true, (string Text, ManualResetEventSlim Gate)? held = null, bool reuse = true)
        {
            Logger = held is var (text, gate) ? new HeldLogger(Log, text, gate) : Log;
            Entry = Catalog.Add("Encoder", deviceKey: deviceKey ? K : null);
            Backend = new MediaFoundationH264(Catalog, Logger, BackendTime, reuse);
            Backends = new H264Backends([Backend], Logger);
        }

        public FakeMfCatalog Catalog { get; } = new();

        public FakeMfEntry Entry { get; }

        /// <summary>What was logged (and recorded: a held line only once its gate is open).</summary>
        public RecordingLogger Log { get; } = new();

        /// <summary>The log the chain and its streams write to.</summary>
        public ILogger Logger { get; }

        public MediaFoundationH264 Backend { get; }

        public H264Backends Backends { get; }

        public ManualTime Time { get; } = new(DateTimeOffset.UnixEpoch);

        /// <summary>
        /// The backend's clock, which these cases never move: its bounds (the probe's 3 s, a frame's wait, a start's 2 s)
        /// are never reached. On the real clock a starved thread pool (the start of a full run) let the probe's bound pass
        /// before the fake encoder's output was read, so the probe failed and the stream was images for good.
        /// </summary>
        public ManualTime BackendTime { get; } = new(DateTimeOffset.UnixEpoch);

        /// <summary>Sessions at the stream's size (a probe's are 322 × 194).</summary>
        public int Sessions(int width = W, int height = H) => Catalog.Ports.Count(p => p.Output?.Width == width && p.Output?.Height == height);

        /// <summary>
        /// A case's end, which a failing case must not leave blocked: the backend's clock is moved past every bound it has
        /// (a start stalled in its probe, on the session's own token, times out and aborts its session), the backends are
        /// stopped (probes cancelled, streams ended, sessions retired), and the pending starts and streams are awaited, at most
        /// <see cref="Limit"/> (real time). The clock moves on each poll, for a bound armed after the first move.
        /// </summary>
        public void Dispose()
        {
            BackendTime.Advance(PastEveryBound);
            var stopped = Backends.StopAsync();
            var watch = Stopwatch.StartNew();
            while (!stopped.IsCompleted && watch.Elapsed < Limit)
            {
                BackendTime.Advance(PastEveryBound);
                Thread.Sleep(5);
            }

            Assert.True(stopped.IsCompleted, "the backends' stop didn't end: a start or a stream is still blocked");
            stopped.GetAwaiter().GetResult();
        }

        private static readonly TimeSpan PastEveryBound = TimeSpan.FromMinutes(10);
    }

    /// <summary>The backend's probe, at most <see cref="Limit"/> (real time): the backend's own bounds are on a clock that never moves.</summary>
    private static Task<H264Probe> Probe(Chain chain) =>
        chain.Backends.ProbeAsync(chain.Backend).WaitAsync(Limit, TestContext.Current.CancellationToken);

    /// <summary>One subscription's stream, fed as the sink feeds it.</summary>
    private sealed class Feed : IDisposable
    {
        private readonly Chain _chain;
        private long _tick;
        private int _refreshes;
        private int _answered;

        public Feed(Chain chain)
        {
            _chain = chain;
            Stream = new VideoStream(chain.Backends, 30, () => { }, () => Interlocked.Increment(ref _refreshes), Skipped, chain.Logger, chain.Time);
        }

        public VideoStream Stream { get; }

        public SkipCounter Skipped { get; } = new();

        public int Refreshes => Volatile.Read(ref _refreshes);

        public List<(long Tick, string Fallback)> Pngs { get; } = [];

        public List<EncodedFrame> Sent { get; } = [];

        /// <summary>The next frame: to the encoder, or recorded as PNG with its reason, which is returned.</summary>
        public string? Frame(int width = W, int height = H)
        {
            var tick = ++_tick;
            var fallback = Stream.Route(width, height, out var offer);
            if (fallback != null)
                Pngs.Add((tick, fallback));
            if (fallback != null && !offer)
                return fallback;
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)tick);
            Stream.Offer(pixels, new FrameHeader
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
            });
            return fallback;
        }

        /// <summary>A frame for each fresh frame asked for since the last answer (a panel that doesn't change).</summary>
        public void Answer(int width = W, int height = H)
        {
            while (Refreshes > _answered)
            {
                _answered++;
                Frame(width, height);
            }
        }

        public List<EncodedFrame> Take()
        {
            var taken = new List<EncodedFrame>();
            while (Stream.TryTake(out var frame))
                taken.Add(frame);
            Sent.AddRange(taken);
            return taken;
        }

        /// <summary>Waits until a frame is queued, answering fresh-frame requests, and takes it.</summary>
        public async Task<EncodedFrame> Next(string what, int width = W, int height = H)
        {
            await Eventually(() =>
            {
                Answer(width, height);
                return Stream.Queued > 0;
            }, what);
            return Take()[^1];
        }

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>
    /// A log whose line containing <paramref name="text"/> waits for <paramref name="gate"/> (at most 10 s, a cap) before
    /// it is recorded: the thread logging it is held there, so what the test sees first comes first.
    /// </summary>
    private sealed class HeldLogger(RecordingLogger log, string text, ManualResetEventSlim gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(text, StringComparison.Ordinal))
                gate.Wait(TimeSpan.FromSeconds(10));
            log.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>
    /// The most a wait lasts: a sanity limit, never the behaviour under test (the stream's times are on the test's clock,
    /// the backend's on one never moved). At a full run's start the thread pool can be starved for over 10 s, and the
    /// fake encoder's work, all on the pool, waits that long; a pool held 12 s made a 10 s limit fail.
    /// </summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>Waits (real time, at most <see cref="Limit"/>) until <paramref name="condition"/>; with <paramref name="time"/>, moves it on 50 ms a poll.</summary>
    private static async Task Eventually(Func<bool> condition, string what, ManualTime? time = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Limit)
                Assert.Fail("Timed out waiting until " + what);
            time?.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AKeyframeRequestOnAStillPanel_FourTimesInARow_IsEachAForcedIdrInTheSameSession()
    {
        using var chain = new Chain();
        using var feed = new Feed(chain);
        feed.Frame();
        Assert.True((await feed.Next("the first keyframe")).Header.Keyframe);
        Assert.True(chain.Backend.ChangesLive);
        feed.Frame();
        Assert.False((await feed.Next("a delta")).Header.Keyframe);

        for (var i = 1; i <= 4; i++)
        {
            feed.Stream.RequestKeyframe();
            var frame = await feed.Next($"keyframe {i}");
            Assert.True(frame.Header.Keyframe, $"request {i} answered");
            Assert.Equal("hardware", frame.Header.Encoder);
        }

        Assert.Equal(1, chain.Sessions());
        var port = chain.Catalog.Ports.Single(p => p.Output?.Width == W);
        // The session's first frame and the four requests, each forced.
        Assert.Equal(5, chain.Catalog.Calls.Count(c => c.Port == port.Id && c.Detail == "FORCE=1"));
    }

    [Fact]
    public async Task WithForcingOff_EachKeyframeRequest_IsANewSession_NotAFailure()
    {
        using var chain = new Chain();
        chain.Entry.Encoder.IgnoreForce = true;
        using var feed = new Feed(chain);
        feed.Frame();
        Assert.True((await feed.Next("the first keyframe")).Header.Keyframe);
        Assert.False(chain.Backend.ChangesLive);
        feed.Frame();
        Assert.False((await feed.Next("a delta")).Header.Keyframe);

        for (var i = 2; i <= 5; i++)
        {
            feed.Stream.RequestKeyframe();
            Assert.True((await feed.Next($"session {i}'s keyframe")).Header.Keyframe);
            Assert.Equal(i, chain.Sessions());
        }

        Assert.False(chain.Backends.IsGivenUp(chain.Backend));
        Assert.DoesNotContain(chain.Log.Messages, m => m.Contains("failed in a stream"));
        Assert.DoesNotContain(chain.Catalog.Calls, c => c.Detail == "FORCE=1" && chain.Catalog.Ports.Single(p => p.Id == c.Port).Output?.Width == W);
    }

    [Fact]
    public async Task AForcedDelta_ReplacesTheSessionOnce_AndCountsOneFailure()
    {
        using var chain = new Chain();
        using var feed = new Feed(chain);
        feed.Frame();
        Assert.True((await feed.Next("the first keyframe")).Header.Keyframe);

        chain.Entry.Encoder.IgnoreForce = true;
        feed.Stream.RequestKeyframe();
        var frame = await feed.Next("the next session's keyframe");
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(2, chain.Sessions());
        Assert.False(chain.Backend.ChangesLive);
        Assert.Single(chain.Log.Messages, m => m.Contains("failed in a stream"));
        Assert.False(chain.Backends.IsGivenUp(chain.Backend));
    }

    [Fact]
    public async Task AStreamsSizeChanges_KeepItsOneActivation_EachNewSizeStartingWithAKeyframe()
    {
        using var chain = new Chain();
        using var feed = new Feed(chain);
        feed.Frame();
        Assert.True((await feed.Next("the first keyframe")).Header.Keyframe);
        var activations = chain.Backend.Activations;
        var port = chain.Catalog.Ports[^1];

        // Six size changes, each a retirement and a new session, all on the first session's activation.
        foreach (var (w, h) in new[] { (80, 48), (64, 48), (96, 64), (80, 48), (64, 48), (96, 64) })
        {
            feed.Frame(w, h);
            var frame = await feed.Next($"the keyframe at {w}x{h}", w, h);
            Assert.True(frame.Header.Keyframe, $"{w}x{h}'s first frame");
            Assert.Equal(("hardware", w, h), (frame.Header.Encoder, frame.Header.PixelWidth, frame.Header.PixelHeight));
        }

        Assert.Equal(activations, chain.Backend.Activations);
        Assert.Same(port, chain.Catalog.Ports[^1]);
        Assert.False(port.ShutDown);
        Assert.DoesNotContain(chain.Log.Messages, m => m.Contains("failed", StringComparison.Ordinal) || m.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWedgedShutdown_GivesMediaFoundationUpForTheStream_AndThreeGiveItUpForTheRun()
    {
        // A healthy session's activation is kept for the next start, never shut down: the shutdown a size change waits
        // for is the one without re-use (a refused renegotiation's).
        using var chain = new Chain(reuse: false);
        var hold = new ManualResetEventSlim();
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                using var feed = new Feed(chain);
                feed.Frame();
                Assert.True((await feed.Next($"stream {i}'s keyframe")).Header.Keyframe);

                // A new size retires the session, whose shutdown never returns: past the bound, Media Foundation is given up for the stream.
                chain.Entry.Encoder.HoldShutdown = hold;
                feed.Frame(80, 48);
                var n = i;
                await Eventually(() => chain.Backends.Quarantines(chain.Backend) == n, "the teardown passes its bound", chain.Time);
                // The third gives it up for the run too: then no backend is left.
                var expected = i < 3 ? VideoStream.Failed : VideoStream.Unavailable;
                await Eventually(() => feed.Frame() == expected, "PNG frames for the stream");
                chain.Entry.Encoder.HoldShutdown = null;
            }

            Assert.True(chain.Backends.IsGivenUp(chain.Backend));
            using var later = new Feed(chain);
            Assert.Equal(VideoStream.Unavailable, later.Frame());
        }
        finally
        {
            hold.Set();
        }
    }

    [Fact]
    public async Task UnsupportedAt4096x2304_IsPngWithEncoderSize_AndMediaFoundationAgainBelowIt()
    {
        using var chain = new Chain();
        chain.Entry.Encoder.OutputTypeHr = t => t.Width == 4096 ? Mf.InvalidMediaType : Mf.SOk;
        using var feed = new Feed(chain);
        feed.Frame(4096, 2304);
        await Eventually(() =>
        {
            feed.Answer(4096, 2304);
            return feed.Pngs.Any(p => p.Fallback == VideoStream.Size);
        }, "PNG frames at that size");
        Assert.Equal(VideoStream.Size, feed.Frame(4096, 2304));

        feed.Frame();
        var frame = await feed.Next("a keyframe below it");
        Assert.True(frame.Header.Keyframe);
        Assert.Equal(W, frame.Header.PixelWidth);
        Assert.False(chain.Backends.IsGivenUp(chain.Backend));
    }

    [Fact]
    public async Task ABusyBegin_BacksOff_ThenMediaFoundationStarts()
    {
        using var chain = new Chain();
        using var feed = new Feed(chain);
        Assert.Equal(H264Probe.Available, await Probe(chain));
        chain.Entry.Encoder.BeginHr = Mf.HwMftFailedStartStreaming;
        var asked = feed.Refreshes;
        feed.Frame();
        // The stream logs the backoff before it settles on PNG (the retry time set, then a fresh frame asked for): the
        // wait is for that ask, its last step, not for the log line alone.
        await Eventually(() => chain.Log.Messages.Any(m => m.Contains("is busy")) && feed.Refreshes > asked, "the backoff, settled");
        Assert.Equal(VideoStream.Failed, feed.Frame());

        chain.Entry.Encoder.BeginHr = Mf.SOk;
        chain.Time.Advance(VideoStream.BusyFirst);
        var sent = feed.Sent.Count;
        feed.Frame();
        await feed.Next("Media Foundation's keyframe after the backoff");
        // The new session's first frame: a delta may follow it (the backoff's end asks for a fresh frame too).
        var frame = feed.Sent[sent];
        Assert.True(frame.Header.Keyframe);
        Assert.Equal("hardware", frame.Header.Encoder);
        Assert.False(chain.Backends.IsGivenUp(chain.Backend));
        Assert.DoesNotContain(chain.Log.Messages, m => m.Contains("failed to start"));
    }

    [Fact]
    public async Task ABusyBeginInAStartsProbe_BacksOff_AndRecoversOnceTheOtherSessionEnds()
    {
        // No device key: every start probes; the chip has one session.
        using var chain = new Chain(deviceKey: false);
        chain.Entry.Encoder.Device = new FakeMfDevice(1);
        var first = new Feed(chain);
        using var second = new Feed(chain);
        try
        {
            first.Frame();
            Assert.True((await first.Next("the first stream's keyframe")).Header.Keyframe);

            var asked = second.Refreshes;
            second.Frame();
            // The backoff is logged before the stream settles on PNG: the wait is for its fresh-frame ask, the last step.
            await Eventually(() => chain.Log.Messages.Any(m => m.Contains("is busy")) && second.Refreshes > asked, "the second stream's start is busy, settled");
            Assert.Equal(VideoStream.Failed, second.Frame());
            Assert.False(chain.Backends.IsGivenUp(chain.Backend));
            Assert.DoesNotContain(chain.Log.Messages, m => m.Contains("failed to start") || m.Contains("failed in a stream"));

            first.Dispose();
            await Eventually(() => chain.Entry.Encoder.Device!.Used == 0, "the first stream's session ends");
            chain.Time.Advance(VideoStream.BusyFirst);
            var sent = second.Sent.Count;
            second.Frame();
            await second.Next("the second stream's keyframe");
            // The new session's first frame: a delta may follow it (the backoff's end asks for a fresh frame too).
            var frame = second.Sent[sent];
            Assert.True(frame.Header.Keyframe);
            Assert.Equal("hardware", frame.Header.Encoder);
        }
        finally
        {
            first.Dispose();
        }
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("unsupported")]
    public async Task ARefusedStartsWedgedTeardown_StartsNoSessionBesideIt_AndPastTheBoundGivesMediaFoundationUpForTheStream(string how)
    {
        // The stream's give-up is logged only once the test has seen the quarantine: it is in the awaited condition,
        // never read straight after it.
        var logged = new ManualResetEventSlim();
        using var chain = new Chain(held: ("given up for this stream", logged));
        Assert.Equal(H264Probe.Available, await Probe(chain));
        var hold = new ManualResetEventSlim();
        var advanced = Task.CompletedTask;
        using var feed = new Feed(chain);
        try
        {
            // A start refused (busy at its begin, or a size the encoder can't take), its teardown's shutdown wedged. The
            // refusal is logged (or recorded) before the stream settles on PNG, which starts a new generation and drops a
            // frame offered before it: the wait is for its fresh-frame ask, the last step, before the next frame is offered.
            chain.Entry.Encoder.HoldShutdown = hold;
            var asked = feed.Refreshes;
            if (how == "busy")
            {
                chain.Entry.Encoder.BeginHr = Mf.HwMftFailedStartStreaming;
                feed.Frame();
                await Eventually(() => chain.Log.Messages.Any(m => m.Contains("is busy")) && feed.Refreshes > asked, "the busy start, settled");
            }
            else
            {
                chain.Entry.Encoder.OutputTypeHr = t => t.Width == 80 ? Mf.InvalidMediaType : Mf.SOk;
                feed.Frame(80, 48);
                await Eventually(() => chain.Backends.IsUnsupported(chain.Backend, 80, 48) && feed.Refreshes > asked, "the unsupported start, settled");
            }

            var refused = chain.Catalog.Ports[^1];
            await Eventually(() => chain.Catalog.Calls.Any(c => c.Port == refused.Id && c.Name == "Shutdown"), "the refused start's shutdown");
            var ports = chain.Catalog.Ports.Count;

            // The backend may be tried again (the backoff over, or another size), but not beside that teardown.
            chain.Entry.Encoder.BeginHr = Mf.SOk;
            chain.Entry.Encoder.OutputTypeHr = _ => Mf.SOk;
            if (how == "busy")
                chain.Time.Advance(VideoStream.BusyFirst);
            // The loop takes the frame and waits for that teardown with its waiting episode open, its grace before the
            // bound (or at it, beside the teardown's timer): the clock passes both at once.
            var graceAt = TimeSpan.FromTicks(chain.Time.GetTimestamp()) + VideoStream.PendingGrace;
            var beside = how == "busy" ? 1 : 0;
            await Eventually(() => chain.Time.TimersDueAt(graceAt) == beside, "the refusal's episode closed");
            feed.Frame();
            await Eventually(() => chain.Time.TimersDueAt(graceAt) == beside + 1, "the retry's waiting episode");

            // Past the bound it is quarantined and Media Foundation is given up for the stream: nothing opened beside it.
            Assert.True(chain.Time.HasTimerDueAt(Quarantine.Bound));
            // The clock moves off the test's thread: what the timers run inline (the loop's give-up) can wait at its log.
            advanced = Task.Run(() => chain.Time.Advance(Quarantine.Bound - TimeSpan.FromTicks(chain.Time.GetTimestamp())), TestContext.Current.CancellationToken);
            await Eventually(() =>
            {
                feed.Answer();
                // The quarantine is counted before the loop goes on (the episode's grace may make frames PNG already):
                // only once it is seen does the loop's give-up reach the log, which the condition waits for too.
                if (chain.Backends.Quarantines(chain.Backend) != 1)
                    return false;
                logged.Set();
                return chain.Log.Messages.Any(m => m.Contains("given up for this stream")) && feed.Frame() == VideoStream.Failed;
            }, "PNG frames once Media Foundation is given up for the stream");
            Assert.Equal(ports, chain.Catalog.Ports.Count);
            Assert.False(chain.Backends.IsGivenUp(chain.Backend));
        }
        finally
        {
            logged.Set();
            hold.Set();
            await advanced.WaitAsync(Limit, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AProbeThatFindsNoWorkingEncoder_SettlesOnUnavailable_AndASecondSubscriptionGetsItWithoutProbing()
    {
        using var chain = new Chain();
        chain.Entry.Encoder.IdrWithoutParameterSets = true;
        using (var feed = new Feed(chain))
        {
            feed.Frame();
            await Eventually(() =>
            {
                feed.Answer();
                return feed.Pngs.Any(p => p.Fallback == VideoStream.Unavailable);
            }, "PNG frames with encoder.unavailable");
            Assert.Equal(VideoStream.Unavailable, feed.Frame());
            Assert.Empty(feed.Take());
        }

        var listings = chain.Catalog.EnumerateFlags.Count;
        using var next = new Feed(chain);
        next.Frame();
        await Eventually(() =>
        {
            next.Answer();
            return next.Pngs.Any(p => p.Fallback == VideoStream.Unavailable);
        }, "the second subscription's encoder.unavailable");
        Assert.Equal(listings, chain.Catalog.EnumerateFlags.Count);
        Assert.Equal(H264Probe.Unavailable, await Probe(chain));
    }
}
