using System.Diagnostics;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using InfoPanel.Tests.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

/// <summary>
/// An <c>h264</c> subscription's sink chooses between its PNG slot and its video stream as one selection: whatever the
/// render thread does between the send loop's reads, frames go out in tick order, and never one older than the last sent.
/// </summary>
public class WebPreviewSinkTests
{
    private const int W = 64;
    private const int H = 48;
    private const string ProfileId = "3dd19c0e-675a-4591-b096-83417853861f";

    private static FrameHeader Header(long tick, int width, int height, string? fallback = null) => new()
    {
        ProfileId = ProfileId, Rev = 1, Tick = tick, FrameId = tick, Encoding = "png", Scale = 1, PixelWidth = width, PixelHeight = height,
        ProfileWidth = width, ProfileHeight = height, ItemBounds = [], EncodeMs = 0, Skipped = 0, Fallback = fallback,
    };

    private static WebPreviewSink VideoSink(FakeH264Backend? backend = null, TimeProvider? time = null)
    {
        var backends = new H264Backends([backend ?? new FakeH264Backend()], NullLogger.Instance);
        return new WebPreviewSink(ProfileId, new FrameRequest(1, 30), () => { }, (_, _) => { }, WebPreviewSink.H264,
            (wake, refresh, skipped) => new VideoStream(backends, 30, wake, refresh, skipped, NullLogger.Instance, time ?? TimeProvider.System));
    }

    /// <summary>
    /// As the render thread does with any frame (<see cref="WebPreviewSink.OnFrame"/>): routed, then offered, or put in the
    /// PNG slot (and offered after it when the stream asks: a waiting episode's PNG frame). Returns the fallback.
    /// </summary>
    private static string? Frame(WebPreviewSink sink, long tick, bool queuePng = true)
    {
        var fallback = sink.Video!.Route(W, H, out var offer);
        if (fallback != null && queuePng)
            sink.QueuePng(Header(tick, W, H, fallback), new byte[] { 1, 2, 3 });
        if (fallback == null || offer)
            sink.Video.Offer(new byte[W * H * 4], Header(tick, W, H));
        return fallback;
    }

    private static FrameHeader Encoded(long tick, bool keyframe) => Header(tick, W, H) with { Encoding = "h264", Keyframe = keyframe };

    /// <summary>
    /// A stream whose encoder's start waits for the test: frame 1 waits for it, the clock passes the grace, and frame
    /// <paramref name="tick"/> is a PNG frame offered too (unless not <paramref name="queuePng"/>: the test puts it in the
    /// slot itself), whose pixels the candidate's keyframe carries once the start is let go.
    /// </summary>
    private static async Task<(WebPreviewSink Sink, ManualResetEventSlim Start)> PastTheGrace(long tick, bool queuePng = true)
    {
        var start = new ManualResetEventSlim();
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var backend = new FakeH264Backend
        {
            Refuse = _ =>
            {
                start.Wait(TimeSpan.FromSeconds(30));
                return null;
            },
        };
        var sink = VideoSink(backend, time);
        Assert.Null(Frame(sink, 1));
        var watch = Stopwatch.StartNew();
        while (!(sink.Video!.Waiting && backend.Starts == 1))
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the first frame waits for the start");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        time.Advance(VideoStream.PendingGrace);
        Assert.Equal(VideoStream.Failed, Frame(sink, tick, queuePng));
        return (sink, start);
    }

    /// <summary>As the render thread does for a frame that fits: routed to video and offered.</summary>
    private static void Video(WebPreviewSink sink, long tick)
    {
        Assert.Null(sink.Video!.Route(W, H, out _));
        sink.Video.Offer(new byte[W * H * 4], Header(tick, W, H));
    }

    /// <summary>As the render thread does for a frame larger than the video size: routed to PNG and put in the PNG slot.</summary>
    private static void Png(WebPreviewSink sink, long tick)
    {
        var fallback = sink.Video!.Route(VideoStream.MaxSide + 1, 16, out _);
        Assert.Equal(VideoStream.Size, fallback);
        sink.QueuePng(Header(tick, VideoStream.MaxSide + 1, 16, fallback), new byte[] { 1, 2, 3 });
    }

    private static void WaitQueued(WebPreviewSink sink, int count)
    {
        var watch = Stopwatch.StartNew();
        while (sink.Video!.Queued < count)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{count} video frames queued");
            Thread.Yield();
        }
    }

    private static FrameHeader Decode(OutgoingFrame frame) => FrameCodec.Decode((byte[])[.. frame.Prefix, .. frame.Payload.Span]).Header;

    private static List<FrameHeader> TakeAll(WebPreviewSink sink)
    {
        var taken = new List<FrameHeader>();
        while (sink.TryTakeFrame(out var frame))
            taken.Add(Decode(frame));
        return taken;
    }

    [Fact]
    public void APngFrameThenVideo_ArrivingBetweenTheSendersReads_GoInTickOrder()
    {
        var sink = VideoSink();
        try
        {
            Video(sink, 1);
            WaitQueued(sink, 1);
            Assert.Equal([1L], TakeAll(sink).Select(h => h.Tick));

            // The sender pauses between its two reads; meanwhile the render thread sends tick 10 as PNG, then tick 11 as
            // video, which is encoded and queued before the sender goes on.
            var once = 0;
            sink.BetweenReads = () =>
            {
                if (Interlocked.Exchange(ref once, 1) != 0)
                    return;
                Png(sink, 10);
                Video(sink, 11);
                WaitQueued(sink, 1);
            };

            var sent = TakeAll(sink);
            Assert.Equal([10L, 11L], sent.Select(h => h.Tick));
            Assert.Equal(["png", "h264"], sent.Select(h => h.Encoding));
            Assert.True(sent[1].Keyframe, "video after PNG frames starts with a keyframe");
        }
        finally
        {
            sink.Retire();
        }
    }

    [Fact]
    public void AFrameOlderThanTheLastSent_IsNeverSent_AndCountsAsSkipped()
    {
        var sink = VideoSink();
        try
        {
            Video(sink, 6);
            WaitQueued(sink, 1);
            Assert.Equal([6L], TakeAll(sink).Select(h => h.Tick));

            // However it got there, a PNG frame of an older tick than the last one sent is dropped, not sent.
            sink.QueuePng(Header(5, W, H, VideoStream.Size), new byte[] { 1, 2, 3 });
            Assert.Empty(TakeAll(sink));

            Png(sink, 7);
            var next = Assert.Single(TakeAll(sink));
            Assert.Equal(7, next.Tick);
            Assert.Equal(1, next.Skipped);
        }
        finally
        {
            sink.Retire();
        }
    }

    // ---- the cutover across both send queues ----

    [Fact]
    public async Task SlowSenderAtTheCutover_APngFrameStillInTheSlot_IsDroppedForTheKeyframeOfItsTick()
    {
        var (sink, start) = await PastTheGrace(2);
        try
        {
            // PNG 2 waits in the slot; keyframe 2 (its pixels) and deltas 3 and 4 reach the video queue; then the sender goes on.
            start.Set();
            WaitQueued(sink, 1);
            Assert.Null(Frame(sink, 3));
            WaitQueued(sink, 2);
            Assert.Null(Frame(sink, 4));
            WaitQueued(sink, 3);

            var sent = TakeAll(sink);
            Assert.Equal([2L, 3L, 4L], sent.Select(h => h.Tick));
            Assert.Equal(["h264", "h264", "h264"], sent.Select(h => h.Encoding));
            Assert.Equal([true, false, false], sent.Select(h => h.Keyframe == true));
            Assert.Equal(1, sent[1].Skipped); // PNG 2.
        }
        finally
        {
            start.Set();
            sink.Retire();
        }
    }

    [Fact]
    public async Task SlowSenderAtTheCutover_APngFrameSentFirst_IsFollowedByTheKeyframeOfItsTick()
    {
        var (sink, start) = await PastTheGrace(2);
        try
        {
            var png = Assert.Single(TakeAll(sink));
            Assert.Equal((2L, "png"), (png.Tick, png.Encoding));

            start.Set();
            WaitQueued(sink, 1);
            Assert.Null(Frame(sink, 3));
            WaitQueued(sink, 2);
            Assert.Null(Frame(sink, 4));
            WaitQueued(sink, 3);
            var sent = TakeAll(sink);
            Assert.Equal([2L, 3L, 4L], sent.Select(h => h.Tick));
            Assert.Equal([true, false, false], sent.Select(h => h.Keyframe == true));
        }
        finally
        {
            start.Set();
            sink.Retire();
        }
    }

    [Fact]
    public void ADeltaAfterAPngFrame_IsNeverSent_AKeyframeIsAsked_AndTheNextKeyframeAndItsDeltasAre()
    {
        var sink = VideoSink();
        var refreshes = 0;
        sink.Refresh = () => Interlocked.Increment(ref refreshes);
        try
        {
            Video(sink, 1);
            WaitQueued(sink, 1);
            Assert.Equal([1L], TakeAll(sink).Select(h => h.Tick));
            Png(sink, 2);
            Assert.Equal([2L], TakeAll(sink).Select(h => h.Tick));

            // A stream that skipped the generation rule: deltas of tick 2 and later, with no keyframe.
            Assert.True(sink.Video!.QueueEncoded(Encoded(2, false), new byte[] { 9 }));
            Assert.True(sink.Video.QueueEncoded(Encoded(3, false), new byte[] { 9 }));
            Assert.Empty(TakeAll(sink));
            Assert.Equal(1, refreshes); // A keyframe asked for (its refresh), once.

            Assert.True(sink.Video.QueueEncoded(Encoded(4, true), new byte[] { 9 }));
            Assert.True(sink.Video.QueueEncoded(Encoded(5, false), new byte[] { 9 }));
            var sent = TakeAll(sink);
            Assert.Equal([4L, 5L], sent.Select(h => h.Tick));
            Assert.Equal(2, sent[0].Skipped);
        }
        finally
        {
            sink.Retire();
        }
    }

    [Fact]
    public void APngFrameOfALaterTick_LeavingVideo_IsSent_AndNoVideoOfTheOldGenerationFollowsIt()
    {
        var sink = VideoSink();
        try
        {
            Video(sink, 1);
            WaitQueued(sink, 1);
            Video(sink, 2);
            WaitQueued(sink, 2);
            Assert.Equal([1L, 2L], TakeAll(sink).Select(h => h.Tick));

            Video(sink, 3);
            WaitQueued(sink, 1);
            Png(sink, 4);
            var sent = TakeAll(sink);
            Assert.Equal([(4L, "png")], sent.Select(h => (h.Tick, h.Encoding)));
        }
        finally
        {
            sink.Retire();
        }
    }

    [Fact]
    public async Task SlowSenderAtTheCutover_WithTheKeyframeHeld_WhenItsPngFrameArrives_TheOrderIsTheSame()
    {
        // The render thread queues PNG 2 late: the sender holds keyframe 2 from its first read when the slot gains it.
        var (sink, start) = await PastTheGrace(2, queuePng: false);
        try
        {
            start.Set();
            WaitQueued(sink, 1);
            Assert.Null(Frame(sink, 3));
            WaitQueued(sink, 2);
            var once = 0;
            sink.BetweenReads = () =>
            {
                if (Interlocked.Exchange(ref once, 1) == 0)
                    sink.QueuePng(Header(2, W, H, VideoStream.Failed), new byte[] { 1, 2, 3 });
            };

            var sent = TakeAll(sink);
            Assert.Equal([2L, 3L], sent.Select(h => h.Tick));
            Assert.Equal([true, false], sent.Select(h => h.Keyframe == true));
        }
        finally
        {
            start.Set();
            sink.Retire();
        }
    }

    [Fact]
    public async Task SlowSenderAtTheCutover_WithThePngFrameHeld_WhenItsKeyframeArrives_TheOrderIsTheSame()
    {
        var (sink, start) = await PastTheGrace(2);
        try
        {
            // An older video frame goes first, so PNG 2 stays held; then keyframe 2 and delta 3 reach the video queue.
            Assert.True(sink.Video!.QueueEncoded(Encoded(1, true), new byte[] { 9 }));
            Assert.True(sink.TryTakeFrame(out var older));
            Assert.Equal(1, Decode(older).Tick);

            start.Set();
            WaitQueued(sink, 1);
            Assert.Null(Frame(sink, 3));
            WaitQueued(sink, 2);
            var sent = TakeAll(sink);
            Assert.Equal([2L, 3L], sent.Select(h => h.Tick));
            Assert.Equal([true, false], sent.Select(h => h.Keyframe == true));
        }
        finally
        {
            start.Set();
            sink.Retire();
        }
    }
}
