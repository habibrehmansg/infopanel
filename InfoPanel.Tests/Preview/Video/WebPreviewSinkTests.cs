using System.Diagnostics;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
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

    private static WebPreviewSink VideoSink()
    {
        var backends = new H264Backends([new FakeH264Backend()], NullLogger.Instance);
        return new WebPreviewSink(ProfileId, new FrameRequest(1, 30), () => { }, (_, _) => { }, WebPreviewSink.H264,
            (wake, refresh, skipped) => new VideoStream(backends, 30, wake, refresh, skipped, NullLogger.Instance, TimeProvider.System));
    }

    /// <summary>As the render thread does for a frame that fits: routed to video and offered.</summary>
    private static void Video(WebPreviewSink sink, long tick)
    {
        Assert.Null(sink.Video!.Route(W, H));
        sink.Video.Offer(new byte[W * H * 4], Header(tick, W, H));
    }

    /// <summary>As the render thread does for a frame larger than the video size: routed to PNG and put in the PNG slot.</summary>
    private static void Png(WebPreviewSink sink, long tick)
    {
        var fallback = sink.Video!.Route(VideoStream.MaxSide + 1, 16);
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
}
