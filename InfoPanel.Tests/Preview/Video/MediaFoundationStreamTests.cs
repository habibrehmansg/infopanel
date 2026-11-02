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
/// stream runs on a clock the test moves; the backend's own bounds on the real clock, which these cases never reach.
/// </summary>
public class MediaFoundationStreamTests
{
    private const int W = 64;
    private const int H = 48;
    private const string K = @"PCI\VEN_1002&DEV_1900&SUBSYS_00311F66&REV_C5\4&1234&0&0041";

    /// <summary>The run's chain: Media Foundation alone, over one catalog.</summary>
    private sealed class Chain
    {
        /// <param name="held">A log line that waits for the gate before it is recorded (<see cref="HeldLogger"/>).</param>
        public Chain(bool deviceKey = true, (string Text, ManualResetEventSlim Gate)? held = null)
        {
            Logger = held is var (text, gate) ? new HeldLogger(Log, text, gate) : Log;
            Entry = Catalog.Add("Encoder", deviceKey: deviceKey ? K : null);
            Backend = new MediaFoundationH264(Catalog, Logger);
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

        /// <summary>Sessions at the stream's size (a probe's are 322 × 194).</summary>
        public int Sessions(int width = W, int height = H) => Catalog.Ports.Count(p => p.Output?.Width == width && p.Output?.Height == height);
    }

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

    [Fact]
    public async Task AKeyframeRequestOnAStillPanel_FourTimesInARow_IsEachAForcedIdrInTheSameSession()
    {
        var chain = new Chain();
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
        var chain = new Chain();
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
        var chain = new Chain();
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
    public async Task AWedgedShutdown_GivesMediaFoundationUpForTheStream_AndThreeGiveItUpForTheRun()
    {
        var chain = new Chain();
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
        var chain = new Chain();
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
        var chain = new Chain();
        using var feed = new Feed(chain);
        Assert.Equal(H264Probe.Available, await chain.Backends.ProbeAsync(chain.Backend));
        chain.Entry.Encoder.BeginHr = Mf.HwMftFailedStartStreaming;
        feed.Frame();
        await Eventually(() => chain.Log.Messages.Any(m => m.Contains("is busy")), "the backoff");
        Assert.Equal(VideoStream.Failed, feed.Frame());

        chain.Entry.Encoder.BeginHr = Mf.SOk;
        chain.Time.Advance(VideoStream.BusyFirst);
        feed.Frame();
        var frame = await feed.Next("Media Foundation's keyframe after the backoff");
        Assert.True(frame.Header.Keyframe);
        Assert.Equal("hardware", frame.Header.Encoder);
        Assert.False(chain.Backends.IsGivenUp(chain.Backend));
        Assert.DoesNotContain(chain.Log.Messages, m => m.Contains("failed to start"));
    }

    [Fact]
    public async Task ABusyBeginInAStartsProbe_BacksOff_AndRecoversOnceTheOtherSessionEnds()
    {
        // No device key: every start probes; the chip has one session.
        var chain = new Chain(deviceKey: false);
        chain.Entry.Encoder.Device = new FakeMfDevice(1);
        var first = new Feed(chain);
        using var second = new Feed(chain);
        try
        {
            first.Frame();
            Assert.True((await first.Next("the first stream's keyframe")).Header.Keyframe);

            second.Frame();
            await Eventually(() => chain.Log.Messages.Any(m => m.Contains("is busy")), "the second stream's start is busy");
            Assert.Equal(VideoStream.Failed, second.Frame());
            Assert.False(chain.Backends.IsGivenUp(chain.Backend));
            Assert.DoesNotContain(chain.Log.Messages, m => m.Contains("failed to start") || m.Contains("failed in a stream"));

            first.Dispose();
            await Eventually(() => chain.Entry.Encoder.Device!.Used == 0, "the first stream's session ends");
            chain.Time.Advance(VideoStream.BusyFirst);
            second.Frame();
            var frame = await second.Next("the second stream's keyframe");
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
        var chain = new Chain(held: ("given up for this stream", logged));
        Assert.Equal(H264Probe.Available, await chain.Backends.ProbeAsync(chain.Backend));
        var hold = new ManualResetEventSlim();
        var advanced = Task.CompletedTask;
        using var feed = new Feed(chain);
        try
        {
            // A start refused (busy at its begin, or a size the encoder can't take), its teardown's shutdown wedged.
            chain.Entry.Encoder.HoldShutdown = hold;
            if (how == "busy")
            {
                chain.Entry.Encoder.BeginHr = Mf.HwMftFailedStartStreaming;
                feed.Frame();
                await Eventually(() => chain.Log.Messages.Any(m => m.Contains("is busy")), "the busy start");
            }
            else
            {
                chain.Entry.Encoder.OutputTypeHr = t => t.Width == 80 ? Mf.InvalidMediaType : Mf.SOk;
                feed.Frame(80, 48);
                await Eventually(() => chain.Backends.IsUnsupported(chain.Backend, 80, 48), "the unsupported start");
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
            await advanced;
        }
    }

    [Fact]
    public async Task AProbeThatFindsNoWorkingEncoder_SettlesOnUnavailable_AndASecondSubscriptionGetsItWithoutProbing()
    {
        var chain = new Chain();
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
        Assert.Equal(H264Probe.Unavailable, await chain.Backends.ProbeAsync(chain.Backend));
    }
}
