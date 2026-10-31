using System.Diagnostics;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// The stream over a backend shaped like an ffmpeg process (<see cref="ProcessLikeBackend"/>), on any machine: its
/// sessions make a keyframe only as they start and take no new frame rate, so a keyframe request and a frame rate
/// change replace the session without counting a failure; and a session whose teardown never ends gives the backend up
/// for the stream instead of another starting beside it.
/// </summary>
public class VideoStreamRestartTests
{
    private const int W = 64;
    private const int H = 48;

    /// <summary>A fake backend that says what an ffmpeg process's backend says of itself.</summary>
    private sealed class ProcessLikeBackend(FakeH264Backend inner) : IH264Backend
    {
        public FakeH264Backend Inner { get; } = inner;

        public string Name => Inner.Name;

        public H264Kind Kind => Inner.Kind;

        public bool ChangesLive => false;

        public bool GivesUpWhenTeardownHangs => true;

        public H264Probe Probe() => Inner.Probe();

        public IH264Session Start(H264Config config, Action<H264Outcome> output) => new KeyframeOnlyAtStart(Inner.Start(config, output));
    }

    /// <summary>A fake session that, as an ffmpeg process, makes its first frame its only keyframe whatever is forced.</summary>
    private sealed class KeyframeOnlyAtStart(IH264Session inner) : IH264Session
    {
        public H264Submit Submit(byte[] pixels, int width, int height, long frameId, bool forceKeyframe, out string? problem) =>
            inner.Submit(pixels, width, height, frameId, false, out problem);

        public int InFlight => inner.InFlight;

        public long Progress => inner.Progress;

        public IReadOnlyList<Task> Released => inner.Released;

        public bool TrySetFrameRate(int fps) => inner.TrySetFrameRate(fps);

        public Task Abort() => inner.Abort();
    }

    private sealed class Harness : IDisposable
    {
        private long _tick;
        private int _refreshes;

        public Harness(bool withSoftware = false)
        {
            Fake = new FakeH264Backend("Process", H264Kind.Software);
            Backend = new ProcessLikeBackend(Fake);
            Next = withSoftware ? new FakeH264Backend("Next", H264Kind.Software) : null;
            Backends = new H264Backends(Next == null ? [Backend] : [Backend, Next], NullLogger.Instance);
            Stream = new VideoStream(Backends, 30, () => { }, () => Interlocked.Increment(ref _refreshes), Skipped, NullLogger.Instance, Time);
        }

        public ManualTime Time { get; } = new(DateTimeOffset.UnixEpoch);
        public FakeH264Backend Fake { get; }
        public ProcessLikeBackend Backend { get; }
        public FakeH264Backend? Next { get; }
        public H264Backends Backends { get; }
        public SkipCounter Skipped { get; } = new();
        public VideoStream Stream { get; }
        public int Refreshes => Volatile.Read(ref _refreshes);

        public string? Frame()
        {
            var tick = ++_tick;
            var fallback = Stream.Route(W, H);
            if (fallback != null)
                return fallback;
            var pixels = new byte[W * H * 4];
            Array.Fill(pixels, (byte)tick);
            Stream.Offer(pixels, new FrameHeader
            {
                ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Tick = tick, FrameId = tick, Encoding = "png", Scale = 1,
                PixelWidth = W, PixelHeight = H, ProfileWidth = W, ProfileHeight = H, ItemBounds = [], EncodeMs = 0, Skipped = 0,
            });
            return null;
        }

        public List<EncodedFrame> Take()
        {
            var taken = new List<EncodedFrame>();
            while (Stream.TryTake(out var frame))
                taken.Add(frame);
            return taken;
        }

        public void Dispose() => Stream.Dispose();
    }

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

    private static Task Settle() => Task.Delay(100, TestContext.Current.CancellationToken);

    [Fact]
    public async Task KeyframeRequests_StartANewSessionEachTime_WithoutCountingAFailure()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "a delta");
        Assert.False(Assert.Single(h.Take()).Header.Keyframe);

        // Five requests within the failure window, each answered by a new session's keyframe (on a still panel: the
        // refresh's frame). Five failures would have given the backend up.
        for (var i = 2; i <= 6; i++)
        {
            var refreshes = h.Refreshes;
            h.Stream.RequestKeyframe();
            Assert.Equal(refreshes + 1, h.Refreshes);
            h.Frame();
            var n = i;
            await Eventually(() => h.Fake.Sessions.Count == n && h.Stream.Queued == 1, $"session {n}'s keyframe");
            var frame = Assert.Single(h.Take());
            Assert.True(frame.Header.Keyframe);
            Assert.Equal("software", frame.Header.Encoder);
            Assert.True(h.Fake.Sessions[n - 2].Aborted, "the session before it was retired");
            Assert.Single(h.Fake.Last.Submitted);
        }

        Assert.False(h.Backends.IsGivenUp(h.Backend));
        Assert.Null(h.Frame());
        await Eventually(() => h.Stream.Queued == 1, "the last session goes on");
        Assert.False(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Equal(6, h.Fake.Sessions.Count);
    }

    [Fact]
    public async Task AKeyframeAskedWhileTheSessionsFirstIsInFlight_IsAnsweredAfterIt_ByANewSession()
    {
        using var h = new Harness();
        h.Fake.Configure = s => s.Hold = h.Fake.Sessions.Count == 0;
        h.Frame();
        await Eventually(() => h.Fake.Sessions.Count == 1 && h.Fake.Last.Submitted.Count == 1, "the first frame is submitted");

        // The session's own keyframe is still coming: a second frame goes to the same session, no restart.
        h.Stream.RequestKeyframe();
        h.Frame();
        await Eventually(() => h.Fake.Last.Submitted.Count == 2, "the second frame goes to the same session");
        await Settle();
        Assert.Single(h.Fake.Sessions);
        h.Fake.Last.Release();
        await Eventually(() => h.Stream.Queued == 2, "both are queued");
        Assert.Equal([true, false], h.Take().Select(f => f.Header.Keyframe));

        // That keyframe was asked before the request: the request is still owed, so the next frame starts a session.
        h.Frame();
        await Eventually(() => h.Fake.Sessions.Count == 2 && h.Stream.Queued == 1, "a new session answers the request");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "and then goes on");
        Assert.False(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Equal(2, h.Fake.Sessions.Count);
    }

    [Fact]
    public async Task AFrameRateChange_StartsANewSessionAtTheNewRate_WithoutAskingTheLiveOne()
    {
        using var h = new Harness();
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();

        var refreshes = h.Refreshes;
        h.Stream.SetFrameRate(15);
        await Eventually(() => h.Fake.Sessions[0].Aborted && h.Refreshes > refreshes, "the session is retired and a fresh frame asked for");
        h.Frame();
        await Eventually(() => h.Fake.Sessions.Count == 2 && h.Stream.Queued == 1, "a session at the new rate");
        Assert.True(Assert.Single(h.Take()).Header.Keyframe);
        Assert.Equal(15, h.Fake.Last.Config.Fps);
        Assert.Empty(h.Fake.Sessions[0].FrameRates);
        Assert.Empty(h.Fake.Last.FrameRates);
        Assert.False(h.Backends.IsGivenUp(h.Backend));
    }

    [Fact]
    public async Task AHungTeardown_GivesTheBackendUpForTheStream_AndNoSessionStartsBesideIt()
    {
        using var h = new Harness(withSoftware: true);
        h.Fake.Configure = s => s.Wedged = true;
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        Assert.Equal("Process", h.Fake.Name);
        h.Take();

        h.Stream.RequestKeyframe();
        h.Frame();
        await Eventually(() => h.Fake.Sessions[0].Aborted, "the restart retires the session");
        // Its teardown never ends: at the bound the stream gives the backend up and takes the next one.
        await Eventually(() => h.Backends.Quarantines(h.Backend) == 1, "the teardown is given up on at the bound", h.Time);
        h.Frame();
        await Eventually(() => h.Next!.Sessions.Count == 1 && h.Stream.Queued >= 1, "the next backend takes the frames");
        var frame = h.Take().Last();
        Assert.True(frame.Header.Keyframe);
        Assert.Single(h.Fake.Sessions);
        h.Fake.Sessions[0].Unwedge();
    }

    [Fact]
    public async Task AHungTeardown_OfTheOnlyBackend_LeavesPngFrames_EncoderFailed()
    {
        using var h = new Harness();
        h.Fake.Configure = s => s.Wedged = true;
        h.Frame();
        await Eventually(() => h.Stream.Queued == 1, "the first keyframe");
        h.Take();

        h.Stream.SetFrameRate(20);
        await Eventually(() => h.Fake.Sessions[0].Aborted, "the rate change retires the session");
        await Eventually(() => h.Backends.Quarantines(h.Backend) == 1, "the teardown is given up on at the bound", h.Time);
        h.Frame();
        await Eventually(() => h.Frame() == VideoStream.Failed, "PNG frames: the stream has no encoder left");
        await Settle();
        Assert.Single(h.Fake.Sessions);
        Assert.False(h.Backends.IsGivenUp(h.Backend), "given up for the stream only");
        h.Fake.Sessions[0].Unwedge();
    }
}
