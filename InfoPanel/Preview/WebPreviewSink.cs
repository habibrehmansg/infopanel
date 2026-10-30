using InfoPanel.Output;
using InfoPanel.Spec;
using System.Threading.Channels;

namespace InfoPanel.Preview;

/// <summary>A frame ready to send: the codec prefix (magic, length, header) and the payload, which go out as one binary message.</summary>
internal sealed record OutgoingFrame(byte[] Prefix, ReadOnlyMemory<byte> Payload, long Rev);

/// <summary>
/// One subscription of a preview socket to one profile; a new <c>subscribe</c> makes a new sink. <see cref="OnFrame"/>
/// (render thread) encodes the PNG once per frame (shared with other sinks of that scale), builds the header and drops
/// it into the sink's own one-slot channel (<see cref="BoundedChannelFullMode.DropOldest"/>), so the render thread
/// never waits for the network: a slow socket gets the newest frame and skips the ones it couldn't send (counted in the
/// next frame's <c>skipped</c>). The socket's send loop reads only the current subscription's channel, and
/// <see cref="Retire"/> closes the channel when the subscription ends, so a late frame of an earlier subscription can
/// never displace the current one's. An <c>h264</c> subscription's frames are PNG frames that say why
/// (<c>encoder.unavailable</c>: this engine has no H.264 encoder).
/// </summary>
internal sealed class WebPreviewSink : IFrameSink
{
    public const string Png = "png";
    public const string H264 = "h264";

    /// <summary>The <c>fallback</c> of an <c>h264</c> subscription's PNG frames when the engine has no working H.264 encoder.</summary>
    public const string Unavailable = "encoder.unavailable";

    private readonly Channel<(FrameHeader Header, ReadOnlyMemory<byte> Png)> _frames;
    private readonly Action _wake;
    private readonly Action<WebPreviewSink, FrameSinkClosedReason> _closed;
    private long _skipped;
    private FrameRequest _request;
    private volatile bool _retired;

    public WebPreviewSink(string profileId, FrameRequest request, Action wake, Action<WebPreviewSink, FrameSinkClosedReason> closed, string encoding = Png)
    {
        ProfileId = profileId;
        Encoding = encoding;
        _request = request;
        _wake = wake;
        _closed = closed;
        _frames = Channel.CreateBounded<(FrameHeader, ReadOnlyMemory<byte>)>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref _skipped));
    }

    public string ProfileId { get; }

    /// <summary>The subscription's <c>encoding</c>: <c>png</c> or <c>h264</c>.</summary>
    public string Encoding { get; }

    public FrameRequest Request
    {
        get => Volatile.Read(ref _request);
        set => Volatile.Write(ref _request, value);
    }

    /// <summary>The client lost its video decoder's state. PNG frames need no keyframe: nothing to do.</summary>
    public void RequestKeyframe()
    {
    }

    /// <summary>The newest frame not yet sent, if any (send loop only), with <c>skipped</c> filled in.</summary>
    public bool TryTakeFrame(out OutgoingFrame frame)
    {
        if (!_frames.Reader.TryRead(out var next))
        {
            frame = null!;
            return false;
        }

        var header = next.Header with { Skipped = Interlocked.Exchange(ref _skipped, 0) };
        frame = new OutgoingFrame(FrameCodec.EncodePrefix(header), next.Png, header.Rev);
        return true;
    }

    /// <summary>The subscription ended: frames that still arrive are dropped.</summary>
    public void Retire()
    {
        _retired = true;
        _frames.Writer.TryComplete();
    }

    public void OnFrame(RenderedFrame frame)
    {
        if (_retired)
            return;

        var png = frame.GetPng();
        var header = FrameHeaders.From(frame, Request.IncludeBounds) with
        {
            EncodeMs = frame.PngMs,
            Fallback = Encoding == H264 ? Unavailable : null,
        };
        if (_frames.Writer.TryWrite((header, png)))
            _wake();
    }

    public void OnClosed(FrameSinkClosedReason reason) => _closed(this, reason);
}
