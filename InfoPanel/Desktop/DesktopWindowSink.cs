using System.Buffers;
using InfoPanel.Output;

namespace InfoPanel.Desktop;

/// <summary>
/// A desktop window's frame sink: scale 1 (one frame pixel per backing pixel; less for a profile over the frame pixel
/// cap, which the window stretches), the default frame rate, no bounds.
/// <see cref="OnFrame"/> (render thread) copies the pixels, which are valid only during the call, into a pooled
/// buffer and leaves it as the one pending frame; the UI thread takes the newest pending frame and shows it. When the
/// UI thread is behind, older pending frames are dropped. A frame whose content hash, size and scale equal the last
/// one taken is skipped.
/// </summary>
public sealed class DesktopWindowSink(Guid profileId, IDesktopPlatform platform, Action<DesktopFrame> show, Action<FrameSinkClosedReason>? closed = null)
    : IFrameSink
{
    /// <summary>What every desktop window asks for: profile pixels at the default frame rate, without item bounds.</summary>
    public static readonly FrameRequest WindowRequest = new(Scale: 1, MaxFps: 0, IncludeBounds: false);

    private DesktopFrame? _pending;
    private ulong _lastHash;
    private int _lastWidth;
    private int _lastHeight;
    private float _lastScale;
    private volatile bool _retired;

    public Guid ProfileId { get; } = profileId;

    public FrameRequest Request => WindowRequest;

    /// <summary>Frames copied by <see cref="OnFrame"/> (for tests).</summary>
    public long FramesReceived;

    public void OnFrame(RenderedFrame frame)
    {
        if (_retired)
            return;

        if (frame.ContentHash == _lastHash && frame.PixelWidth == _lastWidth && frame.PixelHeight == _lastHeight && frame.Scale == _lastScale)
            return;

        using var pixmap = frame.Image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var length = rowBytes * pixmap.Height;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        pixmap.GetPixelSpan().CopyTo(buffer);
        (_lastHash, _lastWidth, _lastHeight, _lastScale) = (frame.ContentHash, pixmap.Width, pixmap.Height, frame.Scale);
        Interlocked.Increment(ref FramesReceived);

        var previous = Interlocked.Exchange(ref _pending, new DesktopFrame(buffer, pixmap.Width, pixmap.Height, rowBytes, frame.Scale));
        if (previous != null)
            ArrayPool<byte>.Shared.Return(previous.Pixels); // the UI thread hasn't taken it yet; its drain is already posted
        else
            platform.Post(Drain);
    }

    public void OnClosed(FrameSinkClosedReason reason)
    {
        if (closed != null)
            platform.Post(() => closed(reason));
    }

    /// <summary>No more frames are shown; a pending one is released.</summary>
    public void Retire()
    {
        _retired = true;
        if (Interlocked.Exchange(ref _pending, null) is { } pending)
            ArrayPool<byte>.Shared.Return(pending.Pixels);
    }

    /// <summary>UI thread: shows the newest pending frame.</summary>
    private void Drain()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } frame)
            return;

        try
        {
            if (!_retired)
                show(frame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame.Pixels);
        }
    }
}
