using InfoPanel.Output;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using System.Threading.Channels;

namespace InfoPanel.Preview;

/// <summary>A frame ready to send: the codec prefix (magic, length, header) and the payload, which go out as one binary message.</summary>
internal sealed record OutgoingFrame(byte[] Prefix, ReadOnlyMemory<byte> Payload, long Rev);

/// <summary>
/// One subscription of a preview socket to one profile; a new <c>subscribe</c> makes a new sink. <see cref="OnFrame"/>
/// runs on the render thread and never waits for the network.
/// <list type="bullet">
/// <item>A <c>png</c> subscription's frame, and an <c>h264</c> subscription's frame that can't go to video (its
/// <c>fallback</c>), is the PNG encoded once per frame (shared with other sinks of that scale), dropped into the sink's
/// own one-slot channel (<see cref="BoundedChannelFullMode.DropOldest"/>): a slow socket gets the newest frame and
/// skips the ones it couldn't send.</item>
/// <item>An <c>h264</c> subscription's other frames go to its <see cref="VideoStream"/>, which drops only before
/// encoding and queues every encoded frame, in order.</item>
/// </list>
/// The socket's send loop reads only the current subscription's sink (both queues as one selection, the older tick first,
/// never a tick older than the last sent: <see cref="TryTakeFrame"/>), and <see cref="Retire"/> ends both when the subscription ends, so a late
/// frame of an earlier subscription can never displace the current one's. <c>skipped</c> counts every frame drawn for
/// the subscription and not sent since the last one sent.
/// </summary>
internal sealed class WebPreviewSink : IFrameSink
{
    public const string Png = "png";
    public const string H264 = "h264";

    private readonly Channel<(FrameHeader Header, ReadOnlyMemory<byte> Png)> _frames;
    private readonly Action _wake;
    private readonly Action<WebPreviewSink, FrameSinkClosedReason> _closed;
    private readonly SkipCounter _skipped = new();
    private readonly VideoStream? _video;
    private FrameRequest _request;
    private volatile bool _retired;

    /// <param name="video">For an <c>h264</c> subscription: makes its video stream from the send loop's wake, a refresh (a fresh frame
    /// from the render loop) and the skip counter. Without it an <c>h264</c> subscription's frames are PNG with <c>encoder.unavailable</c>.</param>
    public WebPreviewSink(string profileId, FrameRequest request, Action wake, Action<WebPreviewSink, FrameSinkClosedReason> closed,
        string encoding = Png, Func<Action, Action, SkipCounter, VideoStream>? video = null)
    {
        ProfileId = profileId;
        _request = request;
        _wake = wake;
        _closed = closed;
        Encoding = encoding;
        _frames = Channel.CreateBounded<(FrameHeader, ReadOnlyMemory<byte>)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest },
            _ => _skipped.Add());
        if (encoding == H264 && video != null)
            _video = video(wake, () => Refresh(), _skipped);
    }

    public string ProfileId { get; }

    /// <summary>The subscription's <c>encoding</c>: <c>png</c> or <c>h264</c>.</summary>
    public string Encoding { get; }

    /// <summary>The video stream of an <c>h264</c> subscription (tests).</summary>
    internal VideoStream? Video => _video;

    /// <summary>Asks the render service for a fresh frame (set by the session once attached; a no-op before).</summary>
    public Action Refresh { get; set; } = () => { };

    public FrameRequest Request
    {
        get => Volatile.Read(ref _request);
        set
        {
            Volatile.Write(ref _request, value);
            _video?.SetFrameRate(value.MaxFps);
        }
    }

    /// <summary>The client lost its decoder's state: the next video frame is a keyframe. Nothing for a <c>png</c> subscription.</summary>
    public void RequestKeyframe() => _video?.RequestKeyframe();

    /// <summary>Tests: called by <see cref="TryTakeFrame"/> between its reads of the two queues.</summary>
    internal Action? BetweenReads;

    // Send loop only: the candidates taken from each queue and not sent yet, and the tick of the last frame sent.
    private EncodedFrame? _heldVideo;
    private (FrameHeader Header, ReadOnlyMemory<byte> Png)? _heldPng;
    private long _lastTick = long.MinValue;

    /// <summary>
    /// The next frame to send, if any (send loop only), with <c>skipped</c> filled in: of the next video frame and the
    /// PNG slot's frame, the older tick first. One selection over both queues: each candidate is held until it is sent, the
    /// video queue is read first (a PNG frame older than a queued video frame was put in the slot before that frame was
    /// offered, so the slot's read after it sees it), and a frame older than the last one sent is never sent.
    /// </summary>
    public bool TryTakeFrame(out OutgoingFrame frame)
    {
        while (true)
        {
            if (_heldVideo == null && _video != null && _video.TryTake(out var video))
                _heldVideo = video;
            BetweenReads?.Invoke();
            while (_frames.Reader.TryRead(out var png))
            {
                // The newest PNG frame wins, as in the slot.
                if (_heldPng != null)
                    _skipped.Add();
                _heldPng = png;
            }

            if (_heldPng is { } heldPng && (_heldVideo == null || heldPng.Header.Tick < _heldVideo.Header.Tick))
            {
                _heldPng = null;
                if (heldPng.Header.Tick < _lastTick)
                {
                    _skipped.Add();
                    continue;
                }

                _lastTick = heldPng.Header.Tick;
                frame = Outgoing(heldPng.Header, heldPng.Png);
                return true;
            }

            if (_heldVideo is { } heldVideo)
            {
                _heldVideo = null;
                if (heldVideo.Header.Tick < _lastTick)
                {
                    // Not expected (leaving video starts a new generation), and not sent: the frames after it may refer
                    // to it, so the client is sent a keyframe.
                    _skipped.Add();
                    _video!.RequestKeyframe();
                    continue;
                }

                _lastTick = heldVideo.Header.Tick;
                frame = Outgoing(heldVideo.Header, heldVideo.Payload);
                return true;
            }

            frame = null!;
            return false;
        }
    }

    private OutgoingFrame Outgoing(FrameHeader header, ReadOnlyMemory<byte> payload) =>
        new(FrameCodec.EncodePrefix(header with { Skipped = _skipped.Take() }), payload, header.Rev);

    /// <summary>The subscription ended: frames that still arrive are dropped, and its video stream stops (at once; its session within the bound).</summary>
    public void Retire()
    {
        _retired = true;
        _frames.Writer.TryComplete();
        _video?.Dispose();
    }

    public void OnFrame(RenderedFrame frame)
    {
        if (_retired)
            return;

        var request = Request;
        string? fallback = null;
        if (Encoding == H264)
        {
            fallback = _video == null ? VideoStream.Unavailable : _video.Route(frame.PixelWidth, frame.PixelHeight);
            if (fallback == null)
            {
                _video!.Offer(frame, request.IncludeBounds);
                return;
            }
        }

        var png = frame.GetPng();
        QueuePng(FrameHeaders.From(frame, request.IncludeBounds) with { EncodeMs = frame.PngMs, Fallback = fallback }, png);
    }

    /// <summary>Puts a PNG frame in the one-frame PNG slot (the newest wins). Render thread; tests.</summary>
    internal void QueuePng(FrameHeader header, ReadOnlyMemory<byte> png)
    {
        if (_frames.Writer.TryWrite((header, png)))
            _wake();
    }

    public void OnClosed(FrameSinkClosedReason reason)
    {
        _video?.Dispose();
        _closed(this, reason);
    }
}
