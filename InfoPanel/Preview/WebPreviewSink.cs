using InfoPanel.Output;
using System.Threading.Channels;

namespace InfoPanel.Preview;

/// <summary>A frame ready to send: the codec prefix (magic, length, header) and the PNG, which go out as one binary message.</summary>
internal sealed record OutgoingFrame(byte[] Prefix, ReadOnlyMemory<byte> Png, long Revision);

/// <summary>
/// One subscription of a preview socket to one profile; a new <c>subscribe</c> makes a new sink. <see cref="OnFrame"/>
/// (render thread) encodes the PNG once per frame (shared with other sinks of that scale), builds the header and drops
/// it into the sink's own one-slot channel (<see cref="BoundedChannelFullMode.DropOldest"/>), so the render thread
/// never waits for the network: a slow socket gets the newest frame and skips the ones it couldn't send. The socket's
/// send loop reads only the current subscription's channel, and <see cref="Retire"/> closes the channel when the
/// subscription ends, so a late frame of an earlier subscription can never displace the current one's.
/// </summary>
internal sealed class WebPreviewSink(Guid profileId, FrameRequest request, Action wake, Action<WebPreviewSink, FrameSinkClosedReason> closed)
    : IFrameSink
{
    private readonly Channel<OutgoingFrame> _frames =
        Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

    private FrameRequest _request = request;
    private volatile bool _retired;

    public Guid ProfileId { get; } = profileId;

    public FrameRequest Request
    {
        get => Volatile.Read(ref _request);
        set => Volatile.Write(ref _request, value);
    }

    /// <summary>The newest frame not yet sent, if any (send loop only).</summary>
    public bool TryTakeFrame(out OutgoingFrame frame) => _frames.Reader.TryRead(out frame!);

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
        var prefix = FrameCodec.EncodePrefix(FrameHeader.From(frame, Request.IncludeBounds));
        if (_frames.Writer.TryWrite(new OutgoingFrame(prefix, png, frame.Revision)))
            wake();
    }

    public void OnClosed(FrameSinkClosedReason reason) => closed(this, reason);
}
