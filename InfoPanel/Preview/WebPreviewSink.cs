using InfoPanel.Output;
using InfoPanel.Preview.Video;
using InfoPanel.Spec;
using System.Threading.Channels;

namespace InfoPanel.Preview;

/// <summary>A frame ready to send: the codec prefix (magic, length, header) and the payload, which go out as one binary message.</summary>
internal sealed record OutgoingFrame(byte[] Prefix, ReadOnlyMemory<byte> Payload, long Rev, PngLease? Lease = null)
{
    /// <summary>The send loop is done with the frame (its send ended, whatever the outcome, or it never began): its payload is no longer read, and its PNG's reference goes back. Once.</summary>
    public void Sent() => Lease?.Release();
}

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
/// never a tick older than the last sent, never a delta after a PNG frame: <see cref="TryTakeFrame"/>), and <see cref="Retire"/> ends both when the subscription ends, so a late
/// frame of an earlier subscription can never displace the current one's. <c>skipped</c> counts every frame drawn for
/// the subscription and not sent since the last one sent.
/// </summary>
internal sealed class WebPreviewSink : IFrameSink
{
    public const string Png = "png";
    public const string H264 = "h264";

    /// <summary>The <c>sensors</c> a subscription takes.</summary>
    public const string Live = "live", Simulated = "simulated";

    private readonly Channel<(FrameHeader Header, ReadOnlyMemory<byte> Png, PngLease? Lease)> _frames;
    private readonly Action _wake;
    private readonly Action<WebPreviewSink, FrameSinkClosedReason> _closed;
    private readonly SkipCounter _skipped = new();
    private readonly VideoStream? _video;
    private volatile bool _retired;

    // The asked request, the PNG pace's frame rate (0: the asked rate) and the request the render loop reads: one state,
    // replaced whole under the lock by its two writers (the setter and the pacing), read whole without it.
    private sealed record RequestState(FrameRequest Asked, int Cap, FrameRequest Effective);

    private readonly Lock _requestLock = new();
    private volatile RequestState _state;

    // PNG pacing (render thread, under the lock): the encode time's running mean.
    private double _pngCostMs = double.NaN;

    /// <summary>The share of the render thread a subscription's PNG encodes may take: past it, it is sent fewer frames a second.</summary>
    public const double PngBudget = 0.3;

    /// <param name="video">For an <c>h264</c> subscription: makes its video stream from the send loop's wake, a refresh (a fresh frame
    /// from the render loop) and the skip counter. Without it an <c>h264</c> subscription's frames are PNG with <c>encoder.unavailable</c>.</param>
    public WebPreviewSink(string profileId, FrameRequest request, Action wake, Action<WebPreviewSink, FrameSinkClosedReason> closed,
        string encoding = Png, Func<Action, Action, SkipCounter, VideoStream>? video = null)
    {
        ProfileId = profileId;
        _state = new RequestState(request, 0, request);
        _wake = wake;
        _closed = closed;
        Encoding = encoding;
        _frames = Channel.CreateBounded<(FrameHeader, ReadOnlyMemory<byte>, PngLease?)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest },
            dropped =>
            {
                _skipped.Add();
                dropped.Item3?.Release();
            });
        if (encoding == H264 && video != null)
            _video = video(wake, () => Refresh(), _skipped);
    }

    public string ProfileId { get; }

    /// <summary>The subscription's <c>encoding</c>: <c>png</c> or <c>h264</c>.</summary>
    public string Encoding { get; }

    /// <summary>The subscription's <c>sensors</c>: <c>live</c>, or <c>simulated</c> (its frames come from the profile's simulated loop).</summary>
    public string Sensors { get; init; } = Live;

    /// <summary>The video stream of an <c>h264</c> subscription (tests).</summary>
    internal VideoStream? Video => _video;

    /// <summary>Asks the render service for a fresh frame (set by the session once attached; a no-op before).</summary>
    public Action Refresh { get; set; } = () => { };

    /// <summary>
    /// What the render loop renders for this subscription: the asked request, with fewer frames a second while its PNG
    /// encodes would take more than <see cref="PngBudget"/> of the render thread at the asked rate (<c>maxFps</c> is the
    /// most frames a second).
    /// </summary>
    public FrameRequest Request
    {
        get => _state.Effective;
        set
        {
            lock (_requestLock)
                _state = State(value, _state.Cap);
            _video?.SetFrameRate(value.MaxFps);
        }
    }

    /// <summary>The request the client asked for, before PNG pacing.</summary>
    public FrameRequest Requested => _state.Asked;

    private static RequestState State(FrameRequest asked, int cap) =>
        new(asked, cap, cap > 0 && cap < RenderLoop.EffectiveFps(asked) ? asked with { MaxFps = cap } : asked);

    /// <summary>
    /// After a PNG frame (render thread): the frame rate its encodes allow, from their running mean. Lowered at once; raised
    /// by a clear step (a quarter, at least 2) or back to the asked rate, so a cost near the edge doesn't keep changing the
    /// request (each change is a fresh frame). A frame that goes to video (<paramref name="encodeMs"/> null) lifts it.
    /// </summary>
    private void PacePng(double? encodeMs)
    {
        // Read, computed and published under the lock: a request the setter publishes meanwhile is never overwritten.
        lock (_requestLock)
        {
            var state = _state;
            var asked = RenderLoop.EffectiveFps(state.Asked);
            int cap;
            if (encodeMs is not { } ms)
            {
                _pngCostMs = double.NaN;
                cap = 0;
            }
            else
            {
                _pngCostMs = double.IsNaN(_pngCostMs) ? ms : _pngCostMs + 0.25 * (ms - _pngCostMs);
                var fits = _pngCostMs > 0 ? (int)Math.Clamp(Math.Floor(PngBudget * 1000 / _pngCostMs), 1, asked) : asked;
                var current = state.Cap > 0 ? state.Cap : asked;
                cap = fits >= asked || fits < current || fits >= current + Math.Max(2, current / 4) ? fits : current;
                if (cap >= asked)
                    cap = 0;
            }

            if (cap != state.Cap)
                _state = State(state.Asked, cap);
        }
    }

    /// <summary>The client lost its decoder's state: the next video frame is a keyframe. Nothing for a <c>png</c> subscription.</summary>
    public void RequestKeyframe() => _video?.RequestKeyframe();

    /// <summary>A frame's PNG encode time in milliseconds: its <see cref="RenderedFrame.PngMs"/> (tests set it, since the real one is wall-clock).</summary>
    internal Func<RenderedFrame, double> EncodeMs { get; set; } = frame => frame.PngMs;

    /// <summary>Tests: called by <see cref="TryTakeFrame"/> between its reads of the two queues.</summary>
    internal Action? BetweenReads;

    // Send loop only: the candidates taken from each queue and not sent yet, the tick of the last frame sent and whether it
    // was video, and whether a keyframe is owed since a PNG frame (asked for once).
    private EncodedFrame? _heldVideo;
    private (FrameHeader Header, ReadOnlyMemory<byte> Png, PngLease? Lease)? _heldPng;
    private long _lastTick = long.MinValue;
    private bool _lastVideo;
    private bool _keyframeOwed;
    private bool _keyframeAsked;

    /// <summary>
    /// The next frame to send, if any (send loop only), with <c>skipped</c> filled in: of the next video frame and the
    /// PNG slot's frame, the older tick first. One selection over both queues: each candidate is held until it is sent, the
    /// video queue is read first (a PNG frame older than a queued video frame was put in the slot before that frame was
    /// offered, so the slot's read after it sees it), and a frame older than the last one sent is never sent. Video and PNG
    /// frames never interleave without a keyframe: after a video frame no PNG frame of its tick goes (at equal ticks the
    /// video goes first: that keyframe carries the PNG frame's pixels), and after a PNG frame no video goes until a keyframe
    /// (a delta is dropped and a keyframe asked for).
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
                if (_heldPng is { } replaced)
                {
                    _skipped.Add();
                    replaced.Lease?.Release();
                }
                _heldPng = png;
            }

            if (_heldPng is { } heldPng && (_heldVideo == null || heldPng.Header.Tick < _heldVideo.Header.Tick))
            {
                _heldPng = null;
                if (heldPng.Header.Tick < _lastTick || (_lastVideo && heldPng.Header.Tick == _lastTick))
                {
                    _skipped.Add();
                    heldPng.Lease?.Release();
                    continue;
                }

                _lastTick = heldPng.Header.Tick;
                _lastVideo = false;
                _keyframeOwed = true;
                frame = Outgoing(heldPng.Header, heldPng.Png, heldPng.Lease);
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

                if (_keyframeOwed && heldVideo.Header.Keyframe != true)
                {
                    // Not expected either (the stream's PNG frames start a new generation): a delta after a PNG frame
                    // refers to nothing the client has.
                    _skipped.Add();
                    if (!_keyframeAsked)
                    {
                        _keyframeAsked = true;
                        _video!.RequestKeyframe();
                    }

                    continue;
                }

                _lastTick = heldVideo.Header.Tick;
                _lastVideo = true;
                if (heldVideo.Header.Keyframe == true)
                    _keyframeOwed = _keyframeAsked = false;
                frame = Outgoing(heldVideo.Header, heldVideo.Payload);
                return true;
            }

            frame = null!;
            return false;
        }
    }

    private OutgoingFrame Outgoing(FrameHeader header, ReadOnlyMemory<byte> payload, PngLease? lease = null) =>
        new(FrameCodec.EncodePrefix(header with { Skipped = _skipped.Take() }), payload, header.Rev, lease);

    /// <summary>The subscription ended: frames that still arrive are dropped, and its video stream stops (at once; its session within the bound).</summary>
    public void Retire()
    {
        _retired = true;
        _frames.Writer.TryComplete();
        // What is still queued goes back; a frame the send loop already holds is left to the collector.
        while (_frames.Reader.TryRead(out var queued))
            queued.Lease?.Release();
        _video?.Dispose();
    }

    public void OnFrame(RenderedFrame frame)
    {
        if (_retired)
            return;

        var request = Request;
        string? fallback = null;
        var offer = false;
        if (Encoding == H264)
        {
            fallback = _video == null ? VideoStream.Unavailable : _video.Route(frame.PixelWidth, frame.PixelHeight, out offer);
            if (fallback == null)
            {
                PacePng(null);
                _video!.Offer(frame, request.IncludeBounds);
                return;
            }
        }

        var png = frame.LeasePng();
        var encodeMs = EncodeMs(frame);
        QueuePng(FrameHeaders.From(frame, request.IncludeBounds) with { EncodeMs = encodeMs, Fallback = fallback }, png);
        PacePng(encodeMs);
        // While a video encoder is chosen, started or retired: the PNG frame goes out, and its pixels to the encoder, so
        // video starts from the newest picture the client has seen.
        if (offer)
            _video!.Offer(frame, request.IncludeBounds);
    }

    /// <summary>Puts a PNG frame in the one-frame PNG slot (the newest wins). Render thread; tests.</summary>
    internal void QueuePng(FrameHeader header, ReadOnlyMemory<byte> png)
    {
        if (_frames.Writer.TryWrite((header, png, null)))
            _wake();
    }

    /// <summary>Puts a PNG frame held by reference in the PNG slot. Render thread; tests.</summary>
    internal void QueuePng(FrameHeader header, PngLease png)
    {
        if (_frames.Writer.TryWrite((header, png.Memory, png)))
            _wake();
        else
            png.Release(); // retired
    }

    public void OnClosed(FrameSinkClosedReason reason)
    {
        _video?.Dispose();
        _closed(this, reason);
    }
}
