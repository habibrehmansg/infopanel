using System;
using System.Threading;
using InfoPanel.Output;

namespace InfoPanel.Panels;

/// <summary>
/// One frame a <see cref="PanelSink"/> copied, for its panel's thread: the pixels (Rgba8888, premultiplied, rows
/// <see cref="RowBytes"/> apart) in one of the sink's two buffers. Given back with <see cref="PanelSink.Return"/>.
/// </summary>
public sealed class PanelFrame(byte[] pixels, int width, int height, int rowBytes, long frameId, PanelRotation rotation)
{
    public byte[] Pixels { get; } = pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int RowBytes { get; } = rowBytes;

    public long FrameId { get; set; } = frameId;

    /// <summary>The document revision the frame was drawn from (<see cref="RenderedFrame.Rev"/>).</summary>
    public long Rev { get; set; }

    /// <summary>The rotation the frame is to be shown at (the device's when it was copied).</summary>
    public PanelRotation Rotation { get; set; } = rotation;
}

/// <summary>
/// A USB panel's frame sink: scale 1, the engine's frame rate setting read on every tick, lowered to the panel's pace
/// (<see cref="PacedFps"/>, its runner's <see cref="PanelPacer"/>), no bounds. <see cref="OnFrame"/> (render thread)
/// copies the pixels into one of two buffers and leaves it as the one pending frame, replacing a pending one the panel's
/// thread hasn't taken; it never waits for the panel and takes no lock but its own, held only to swap a buffer. A frame
/// whose content hash, size and rotation equal the last one copied is skipped. The panel's thread takes the newest frame
/// with <see cref="Take"/>.
/// </summary>
public sealed class PanelSink : IFrameSink
{
    private readonly Func<int> _frameRate;
    private readonly Lock _lock = new();
    private readonly PanelFrame?[] _free = new PanelFrame?[2];
    private PanelFrame? _pending;
    private volatile FrameRequest _request;
    private volatile int _paced = 60;
    private volatile int _rotation;
    private ulong _lastHash;
    private int _lastWidth;
    private int _lastHeight;
    private PanelRotation _lastRotation;
    private bool _copied;
    private volatile bool _retired;

    /// <param name="profileId">The profile the panel shows.</param>
    /// <param name="frameRate">The engine's frame rate setting.</param>
    /// <param name="rotation">The device's rotation.</param>
    /// <param name="arrived">Called (on the render thread) after a frame is left pending: wakes the panel's thread.</param>
    /// <param name="closed">Called once when the render service detaches the sink (<see cref="IFrameSink.OnClosed"/>).</param>
    public PanelSink(string profileId, Func<int> frameRate, PanelRotation rotation, Action? arrived = null, Action<FrameSinkClosedReason>? closed = null)
    {
        ProfileId = profileId;
        _frameRate = frameRate;
        _rotation = (int)rotation;
        Arrived = arrived;
        Closed = closed;
        _request = PanelRequest(frameRate());
    }

    public string ProfileId { get; }

    private Action? Arrived { get; }

    private Action<FrameSinkClosedReason>? Closed { get; }

    /// <summary>The most frames a second the panel is sent, by its write times (<see cref="PanelPacer"/>); 60 until it is measured.</summary>
    public int PacedFps
    {
        get => _paced;
        set => _paced = Math.Clamp(value, 1, 60);
    }

    /// <summary>The device's rotation: a change makes the next frame count as new, whatever its content.</summary>
    public PanelRotation Rotation
    {
        get => (PanelRotation)_rotation;
        set => _rotation = (int)value;
    }

    /// <summary>Profile pixels at the frame rate setting (or the panel's pace, when lower), without item bounds.</summary>
    public FrameRequest Request
    {
        get
        {
            var fps = Math.Min(_frameRate(), _paced);
            var request = _request;
            return request.MaxFps == fps ? request : _request = PanelRequest(fps);
        }
    }

    private static FrameRequest PanelRequest(int fps) => new(Scale: 1, MaxFps: fps, IncludeBounds: false);

    /// <summary>Frames copied by <see cref="OnFrame"/> (for tests).</summary>
    public long FramesCopied;

    public void OnFrame(RenderedFrame frame)
    {
        if (_retired)
            return;

        var rotation = Rotation;
        if (_copied && frame.ContentHash == _lastHash && frame.PixelWidth == _lastWidth && frame.PixelHeight == _lastHeight && rotation == _lastRotation)
            return;

        using var pixmap = frame.Image.PeekPixels();
        var rowBytes = pixmap.RowBytes;
        var length = rowBytes * pixmap.Height;

        // The pending frame (not yet taken) is overwritten; else a free buffer; the panel's thread holds at most one.
        PanelFrame? target;
        lock (_lock)
        {
            target = _pending;
            _pending = null;
            if (target == null)
            {
                for (var i = 0; i < _free.Length && target == null; i++)
                {
                    target = _free[i];
                    _free[i] = null;
                }
            }
        }

        if (target == null || target.Pixels.Length < length)
            target = new PanelFrame(new byte[length], pixmap.Width, pixmap.Height, rowBytes, frame.FrameId, rotation);
        else if (target.Width != pixmap.Width || target.Height != pixmap.Height || target.RowBytes != rowBytes)
            target = new PanelFrame(target.Pixels, pixmap.Width, pixmap.Height, rowBytes, frame.FrameId, rotation);
        else
        {
            target.FrameId = frame.FrameId;
            target.Rotation = rotation;
        }

        target.Rev = frame.Rev;
        pixmap.GetPixelSpan()[..length].CopyTo(target.Pixels);
        (_lastHash, _lastWidth, _lastHeight, _lastRotation, _copied) = (frame.ContentHash, pixmap.Width, pixmap.Height, rotation, true);
        Interlocked.Increment(ref FramesCopied);

        lock (_lock)
        {
            if (_retired)
                return;
            _pending = target;
        }
        Arrived?.Invoke();
    }

    public void OnClosed(FrameSinkClosedReason reason) => Closed?.Invoke(reason);

    /// <summary>The panel's thread: the newest pending frame, or null. Give it back with <see cref="Return"/>.</summary>
    public PanelFrame? Take()
    {
        lock (_lock)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    /// <summary>The panel's thread: a frame it took is free again.</summary>
    public void Return(PanelFrame frame)
    {
        lock (_lock)
        {
            for (var i = 0; i < _free.Length; i++)
            {
                if (_free[i] == null)
                {
                    _free[i] = frame;
                    return;
                }
            }
        }
    }

    /// <summary>Buffers the sink holds now, pending and free (for tests: never more than two).</summary>
    public int BuffersHeld
    {
        get
        {
            lock (_lock)
                return (_pending != null ? 1 : 0) + (_free[0] != null ? 1 : 0) + (_free[1] != null ? 1 : 0);
        }
    }

    /// <summary>No more frames are copied; the buffers are let go.</summary>
    public void Retire()
    {
        _retired = true;
        lock (_lock)
        {
            _pending = null;
            _free[0] = _free[1] = null;
        }
    }
}
