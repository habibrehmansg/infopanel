using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Panels;

/// <summary>How a <see cref="PanelRunner"/> ended: stopped when asked, or failed with a status and why.</summary>
public sealed record PanelRunEnd(bool Failed, UsbStatus Status, string Message, FrameSinkClosedReason? SinkClosed = null);

/// <summary>Timings a <see cref="PanelRunner"/> keeps: the keep-alive (an unchanged frame re-sent) and how often it logs its stats.</summary>
public sealed record PanelRunnerOptions
{
    /// <summary>The last frame is written again after this long without a new one; <see cref="Timeout.InfiniteTimeSpan"/>: never.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The first stats line after the start, then one every <see cref="StatsEvery"/>.</summary>
    public TimeSpan FirstStats { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan StatsEvery { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Called on the panel's thread right after each JPEG encode (tests: to stand for the encode's time on a fake clock); null: nothing.</summary>
    public Action? AfterEncode { get; init; }
}

/// <summary>
/// One running BeadaPanel, on a thread of its own: the start sequence, then frames from its <see cref="PanelSink"/>
/// (transformed to the panel's size and rotation, as RGB565, or on a JPEG stream as RGBA encoded once as JPEG), the
/// brightness re-sent between frames when it changed, the last frame re-sent (not encoded again) after
/// <see cref="PanelRunnerOptions.KeepAlive"/> without a new one; on any stop, the screen-off. Waits go through the
/// <see cref="TimeProvider"/>. Its pacing (InfoPanel 1.4's): the sink asks for at most 1000 / (0.9 × the mean frame time)
/// frames a second, checked once a second; the fps is writes per 0.5 s, the frame time the mean of the last 60 writes,
/// each with its encode on a JPEG stream.
/// </summary>
public sealed class PanelRunner
{
    private readonly IUsbBackend _backend;
    private readonly UsbDeviceId _usb;
    private readonly string _label;
    private readonly Func<PanelSink, bool> _attach;
    private readonly Action<PanelSink> _detach;
    private readonly Action<PanelSink> _requestChanged;
    private readonly Action<PanelSink> _refresh;
    private readonly Func<int> _frameRate;
    private readonly TimeProvider _time;
    private readonly PanelRunnerOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource<PanelRunEnd> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly double[] _writeMs = new double[60];
    private readonly Lock _sinkLock = new();
    private volatile int _brightness;
    private volatile int _rotation;
    private volatile string _profileId;
    private volatile StrongBox<FrameSinkClosedReason>? _sinkClosed;
    private readonly PanelEncoding _encoding;
    private volatile bool _jpeg;
    private volatile StrongBox<UsbStatus>? _jpegRefused;
    private long _encodes;
    private PanelSink? _attached;
    private long _stopAskedAt = -1;
    private int _writes;
    private long _lastFrameId;
    private long _turns;
    private long _frameId;
    private long _fpsWindowStart;
    private int _fpsWindowWrites;
    private double _fps;
    private double _frameTimeMs;

    /// <param name="attach">Attaches the sink to the render service (false: the profile can't be rendered now).</param>
    /// <param name="encoding">How frames are sent (<see cref="BeadaPanelProtocol.StreamsJpeg"/>); raw unless given.</param>
    public PanelRunner(IUsbBackend backend, UsbDeviceId usb, string label, string profileId, int brightness, PanelRotation rotation,
        Func<PanelSink, bool> attach, Action<PanelSink> detach, Action<PanelSink> requestChanged, Action<PanelSink> refresh, Func<int> frameRate,
        TimeProvider time, PanelRunnerOptions options, ILogger logger, PanelEncoding encoding = PanelEncoding.Raw)
    {
        _backend = backend;
        _usb = usb;
        _label = label;
        _profileId = profileId;
        _brightness = brightness;
        _rotation = (int)rotation;
        _attach = attach;
        _detach = detach;
        _requestChanged = requestChanged;
        _refresh = refresh;
        _frameRate = frameRate;
        _time = time;
        _options = options;
        _logger = logger;
        _encoding = encoding;
    }

    /// <summary>
    /// The profile the panel shows. Set while running, the session is kept: the thread detaches its sink from the old
    /// profile and attaches a new one to this (its first frame always written, whatever the last one was).
    /// </summary>
    public string ProfileId
    {
        get => _profileId;
        set
        {
            _profileId = value;
            Wake();
        }
    }

    /// <summary>True once the session has started as a JPEG stream, and still after it ends (false before, and on a raw stream).</summary>
    public bool Jpeg => _jpeg;

    /// <summary>Why the panel didn't take the JPEG start tag, so this session went raw; null otherwise.</summary>
    public UsbStatus? JpegRefused => _jpegRefused?.Value;

    /// <summary>Frames encoded as JPEG (for tests: a frame re-sent isn't encoded again).</summary>
    public long Encodes => Interlocked.Read(ref _encodes);

    /// <summary>The panel's sink once the start sequence has run (null before).</summary>
    public PanelSink? Sink { get; private set; }

    /// <summary>True once the panel has started and its sink is attached.</summary>
    public bool Running { get; private set; }

    /// <summary>Completes when the thread has ended, the screen off.</summary>
    public Task<PanelRunEnd> Ended => _ended.Task;

    /// <summary>Frames written a second, over the last 0.5 s window (0 until measured).</summary>
    public double Fps => Volatile.Read(ref _fps);

    /// <summary>The mean of the last 60 write times (a JPEG frame's with its encode), in milliseconds.</summary>
    public double FrameTimeMs => Volatile.Read(ref _frameTimeMs);

    /// <summary>The render service's id of the last frame written (0 before the first).</summary>
    public long LastFrameId => Interlocked.Read(ref _lastFrameId);

    /// <summary>How many times the thread went round its loop (for tests: it sleeps while there is nothing to do).</summary>
    public long Turns => Interlocked.Read(ref _turns);

    /// <summary>Frames written, keep-alives included.</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>The brightness (0–100), sent between frames when it changed.</summary>
    public int Brightness
    {
        get => _brightness;
        set
        {
            _brightness = Math.Clamp(value, 0, 100);
            Wake();
        }
    }

    /// <summary>The rotation, applied from the next frame (the render loop is asked for one at once).</summary>
    public PanelRotation Rotation
    {
        get => (PanelRotation)_rotation;
        set
        {
            PanelSink? sink;
            lock (_sinkLock) // with the sink's making, so a sink made now has this rotation
            {
                _rotation = (int)value;
                sink = Sink;
                if (sink != null)
                    sink.Rotation = value;
            }
            if (sink != null)
                _refresh(sink); // the loop's next frame, whatever the sink last had
        }
    }

    public void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = $"Panel {_label}" };
        thread.Start();
    }

    /// <summary>
    /// Asks the thread to stop: no transfer is started after this but the screen-off, which turns the screen off within
    /// <see cref="BeadaPanelSession.ScreenOffBound"/> of the first ask; then it lets the panel go and ends (<see cref="Ended"/>).
    /// </summary>
    public void RequestStop()
    {
        Interlocked.CompareExchange(ref _stopAskedAt, _time.GetTimestamp(), -1);
        _stop.Cancel();
        Wake();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void Run()
    {
        var end = new PanelRunEnd(false, UsbStatus.Ok, "stopped");
        BeadaPanelSession? session = null;
        try
        {
            // A refused JPEG start is kept at once, so the manager remembers it whatever the start comes to.
            var start = BeadaPanelSession.Start(_backend, _usb, _brightness, _encoding, _time, _stop.Token, refused =>
            {
                _jpegRefused = new StrongBox<UsbStatus>(refused);
                _logger.Information("Panel {Panel}: the JPEG start wasn't taken ({Status}): raw frames, for this panel until the engine restarts", _label, refused);
            });
            if (start.Session is null)
            {
                end = new PanelRunEnd(true, start.Status, start.Message);
                return;
            }
            session = start.Session;
            _jpeg = session.Jpeg;
            end = Loop(session);
        }
        catch (OperationCanceledException)
        {
            end = new PanelRunEnd(false, UsbStatus.Ok, "stopped");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Panel {Panel}: its thread failed", _label);
            end = new PanelRunEnd(true, UsbStatus.Io, ex.Message);
        }
        finally
        {
            Running = false;
            if (_attached is { } sink)
            {
                _attached = null;
                _detach(sink);
                sink.Retire();
            }
            try
            {
                var askedAt = Interlocked.Read(ref _stopAskedAt);
                session?.Stop(askedAt >= 0 ? askedAt : null);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Panel {Panel}: its screen off failed", _label);
            }
            if (session != null)
                _logger.Information("Panel {Panel}: stopped after {Writes} frames ({Fps:0.#} fps, {FrameTime:0.#} ms a frame)", _label, Writes, Fps, FrameTimeMs);
            _ended.TrySetResult(end);
        }
    }

    /// <summary>
    /// A new sink for <paramref name="profileId"/>, published as <see cref="Sink"/> and attached (it replaces the one
    /// attached before, which is detached first). Null when the profile can't be rendered now.
    /// </summary>
    private PanelSink? AttachSink(string profileId)
    {
        if (_attached is { } old)
        {
            _attached = null;
            _detach(old);
            old.Retire();
        }

        PanelSink? made = null;
        lock (_sinkLock) // with the rotation's setter: a rotation set while the sink is made reaches it
        {
            made = new PanelSink(profileId, _frameRate, Rotation, Wake, reason =>
            {
                if (!ReferenceEquals(Sink, made))
                    return; // a sink this runner has moved on from
                _sinkClosed = new StrongBox<FrameSinkClosedReason>(reason);
                RequestStop();
            });
            Sink = made;
            made.Rotation = Rotation;
        }
        if (!_attach(made))
            return null;
        _attached = made;
        return made;
    }

    private PanelRunEnd Loop(BeadaPanelSession session)
    {
        var profileId = ProfileId;
        if (AttachSink(profileId) is not { } sink)
            return new PanelRunEnd(true, UsbStatus.Ok, $"profile {profileId} can't be rendered", FrameSinkClosedReason.ProfileDeleted);
        Running = true;
        var jpeg = session.Jpeg;
        _jpeg = jpeg;
        _logger.Information("Panel {Panel}: running, {Width} x {Height}, {Encoding:l}, profile {ProfileId}", _label, session.Width, session.Height,
            jpeg ? $"JPEG q{BeadaPanelProtocol.JpegQuality}" : "raw RGB565", profileId);

        // A raw stream writes the transformed frame; a JPEG one encodes it once, and a re-send writes the same JPEG.
        var format = jpeg ? PanelPixelFormat.Rgba8888 : PanelPixelFormat.Rgb565Le;
        var target = new byte[PanelTransform.TargetLength(session.Width, session.Height, format)];
        var encoded = Array.Empty<byte>();
        var encodedLength = 0;
        var written = false;
        long lastWrite = 0;
        var started = _time.GetTimestamp();
        var nextStats = _options.FirstStats;
        var lastPace = started;
        _fpsWindowStart = started;

        while (!_stop.IsCancellationRequested)
        {
            Interlocked.Increment(ref _turns);
            if (ProfileId != profileId)
            {
                // Another profile: the session stays, the sink moves to it, and nothing is re-sent until its first frame.
                profileId = ProfileId;
                if (AttachSink(profileId) is not { } moved)
                    return new PanelRunEnd(true, UsbStatus.Ok, $"profile {profileId} can't be rendered", FrameSinkClosedReason.ProfileDeleted);
                sink = moved;
                written = false;
                _logger.Information("Panel {Panel}: profile {ProfileId}", _label, profileId);
            }

            var status = session.SetBrightness(_brightness);
            if (status != UsbStatus.Ok)
                return Failed(status, "the brightness wasn't taken");
            if (_stop.IsCancellationRequested)
                break; // asked to stop during the brightness: no frame now, the screen-off next

            var frame = sink.Take();
            long? encodeStarted = null;
            if (frame != null)
            {
                try
                {
                    Transform(frame, target, session.Width, session.Height, format);
                    _frameId = frame.FrameId;
                }
                finally
                {
                    sink.Return(frame);
                }
                if (jpeg)
                {
                    encodeStarted = _time.GetTimestamp();
                    encodedLength = PanelJpeg.Encode(target, session.Width, session.Height, BeadaPanelProtocol.JpegQuality, ref encoded);
                    Interlocked.Increment(ref _encodes);
                    _options.AfterEncode?.Invoke();
                }
            }
            else if (!(written && _options.KeepAlive != Timeout.InfiniteTimeSpan && _time.GetElapsedTime(lastWrite) >= _options.KeepAlive))
            {
                // Until a frame comes, a stop or a brightness change, or the keep-alive is due (none before the first write).
                WaitForWork(written && _options.KeepAlive != Timeout.InfiniteTimeSpan
                    ? _options.KeepAlive - _time.GetElapsedTime(lastWrite)
                    : Timeout.InfiniteTimeSpan);
                continue;
            }

            if (_stop.IsCancellationRequested)
                break;
            // A JPEG frame's time counts its encode too: the panel's thread spends both on it.
            var before = encodeStarted ?? _time.GetTimestamp();
            status = session.WriteFrame(jpeg ? encoded.AsSpan(0, encodedLength) : target);
            if (status != UsbStatus.Ok)
                return Failed(status, "a frame wasn't taken");
            lastWrite = _time.GetTimestamp();
            written = true;
            Interlocked.Exchange(ref _lastFrameId, _frameId);
            Count(_time.GetElapsedTime(before, lastWrite).TotalMilliseconds, lastWrite);

            if (_time.GetElapsedTime(lastPace) >= TimeSpan.FromSeconds(1))
            {
                lastPace = _time.GetTimestamp();
                var paced = FrameTimeMs > 0 ? (int)Math.Floor(1000 / (0.9 * FrameTimeMs)) : 60;
                if (Math.Clamp(paced, 1, 60) != sink.PacedFps)
                {
                    sink.PacedFps = paced;
                    _requestChanged(sink);
                }
            }

            if (_time.GetElapsedTime(started) >= nextStats)
            {
                nextStats += _options.StatsEvery;
                _logger.Information("Panel {Panel}: {Fps:0.#} fps, {FrameTime:0.#} ms a frame", _label, Fps, FrameTimeMs);
            }
        }

        return _sinkClosed?.Value is { } closed
            ? new PanelRunEnd(true, UsbStatus.Ok, $"profile {ProfileId} closed ({closed})", closed)
            : new PanelRunEnd(false, UsbStatus.Ok, "stopped");
    }

    private PanelRunEnd Failed(UsbStatus status, string what)
    {
        _logger.Information("Panel {Panel}: {What} ({Status})", _label, what, status);
        return new PanelRunEnd(true, status, $"{what} ({status})");
    }

    private void WaitForWork(TimeSpan due)
    {
        if (due == Timeout.InfiniteTimeSpan)
        {
            _wake.Wait();
            return;
        }
        if (due <= TimeSpan.Zero)
            return;
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var woken = _wake.WaitAsync(delay.Token);
        var timer = Task.Delay(due, _time, delay.Token);
        try
        {
            Task.WaitAny(woken, timer);
        }
        finally
        {
            // A wake the cancelled wait still took is harmless: the loop looks for a frame before it waits again.
            delay.Cancel();
        }
    }

    private void Count(double ms, long now)
    {
        var n = Interlocked.Increment(ref _writes);
        _writeMs[(n - 1) % _writeMs.Length] = ms;
        var count = Math.Min(n, _writeMs.Length);
        double sum = 0;
        for (var i = 0; i < count; i++)
            sum += _writeMs[i];
        Volatile.Write(ref _frameTimeMs, sum / count);

        _fpsWindowWrites++;
        var window = _time.GetElapsedTime(_fpsWindowStart, now);
        if (window >= TimeSpan.FromMilliseconds(500))
        {
            Volatile.Write(ref _fps, Math.Clamp(_fpsWindowWrites / window.TotalSeconds, 0, 60));
            _fpsWindowStart = now;
            _fpsWindowWrites = 0;
        }
    }

    /// <summary>The copied frame (Rgba8888, premultiplied) into the panel's buffer (RGB565, or opaque RGBA to encode), at its rotation.</summary>
    private static unsafe void Transform(PanelFrame frame, byte[] target, int width, int height, PanelPixelFormat format)
    {
        fixed (byte* pixels = frame.Pixels)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul), (IntPtr)pixels, frame.RowBytes);
            PanelTransform.Render(pixmap, target, width, height, frame.Rotation, format);
        }
    }
}
