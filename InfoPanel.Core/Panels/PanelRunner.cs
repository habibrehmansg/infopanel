using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Output;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Panels;

/// <summary>How a <see cref="PanelRunner"/> ended: stopped when asked, or failed with a status and why; <c>Code</c>: the reason's code when it isn't the status's (<c>driver</c>: a hosted driver couldn't be reached).</summary>
public sealed record PanelRunEnd(bool Failed, UsbStatus Status, string Message, FrameSinkClosedReason? SinkClosed = null, string? Code = null);

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
/// One running panel, on a thread of its own, through its driver's <see cref="IPanelSession"/>: the start
/// (<see cref="IPanelDriver.Open"/>), then frames from its <see cref="PanelSink"/> (transformed to the panel's size and
/// rotation, as RGB565, or on a JPEG stream as RGBA encoded once as JPEG), the
/// brightness re-sent between frames when it changed, the last frame re-sent (not encoded again) after
/// <see cref="PanelRunnerOptions.KeepAlive"/> without a new one; on any stop, the screen-off. Waits go through the
/// <see cref="TimeProvider"/>. Its pacing (<see cref="PanelPacer"/>, a new one each session and each change of the frame
/// rate setting), checked once a second: the sink asks for at most the panel's ceiling (its session's
/// <see cref="PanelCapabilities.MaxFps"/> under 60, a T113's; kept at the write from the first frame), under what the thread
/// can write (0.85 × 1000 / the mean frame time), and, once the device's buffer is seen to fill, under what the device
/// showed (each write's <see cref="PanelWrite.DeviceTime"/>, as the driver measured its transfer), so a change waits
/// behind a frame or two, not a buffer; the thread itself writes no sooner than one interval of
/// the lower of the setting and the pace after its last write began. The fps is writes per 0.5 s, the frame time the mean
/// of the last 60 writes, each with its encode on a JPEG stream. The log has a line for each new document
/// revision written and for each change of pace of more than a fifth (at most one every <see cref="PaceLogEvery"/>).
/// </summary>
public sealed class PanelRunner
{
    /// <summary>The least time between two lines in the log about the pace.</summary>
    public static readonly TimeSpan PaceLogEvery = TimeSpan.FromSeconds(5);

    private readonly IPanelDriver _driver;
    private readonly PanelCandidate _device;
    private readonly PanelIdentity _identity;
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
    private PanelPacer _pacer = new(null);
    private readonly Lock _sinkLock = new();
    private volatile int _brightness;
    private volatile int _rotation;
    private volatile string _profileId;
    private volatile StrongBox<FrameSinkClosedReason>? _sinkClosed;
    private readonly PanelEncoding _encoding;
    private readonly System.Text.Json.JsonElement _familySettings;
    private volatile bool _jpeg;
    private volatile int _maxFps;
    private volatile StrongBox<UsbStatus>? _jpegRefused;
    private long _encodes;
    private PanelSink? _attached;
    private long _stopAskedAt = -1;
    private int _writes;
    private long _lastFrameId;
    private long _turns;
    private long _frameId;
    private long _frameRev;
    private long _fpsWindowStart;
    private int _fpsWindowWrites;
    private double _fps;
    private double _frameTimeMs;

    /// <summary>A BeadaPanel at <paramref name="usb"/>, through the built-in <see cref="BeadaPanelDriver"/> (its start reads the panel's info itself).</summary>
    /// <param name="attach">Attaches the sink to the render service (false: the profile can't be rendered now).</param>
    /// <param name="encoding">How frames are sent (<see cref="BeadaPanelProtocol.StreamsJpeg"/>); raw unless given.</param>
    public PanelRunner(IUsbBackend backend, UsbDeviceId usb, string label, string profileId, int brightness, PanelRotation rotation,
        Func<PanelSink, bool> attach, Action<PanelSink> detach, Action<PanelSink> requestChanged, Action<PanelSink> refresh, Func<int> frameRate,
        TimeProvider time, PanelRunnerOptions options, ILogger logger, PanelEncoding encoding = PanelEncoding.Raw)
        : this(new BeadaPanelDriver(backend, time), PanelManager.CandidateOf(usb), new PanelIdentity("", 0, 0, null, null), label, profileId, brightness,
            rotation, attach, detach, requestChanged, refresh, frameRate, time, options, logger, encoding)
    {
    }

    /// <summary>The panel <paramref name="driver"/> found at <paramref name="device"/> as <paramref name="identity"/>.</summary>
    /// <param name="attach">Attaches the sink to the render service (false: the profile can't be rendered now).</param>
    /// <param name="encoding">
    /// The format asked of the driver: <see cref="PanelEncoding.Auto"/> none (its own choice), <see cref="PanelEncoding.Jpeg"/>
    /// JPEG, <see cref="PanelEncoding.Raw"/> RGB565; raw unless given.
    /// </param>
    /// <param name="familySettings">The family's settings the panel has set (an object; none: undefined), for the driver's <c>Open</c>.</param>
    public PanelRunner(IPanelDriver driver, PanelCandidate device, PanelIdentity identity, string label, string profileId, int brightness, PanelRotation rotation,
        Func<PanelSink, bool> attach, Action<PanelSink> detach, Action<PanelSink> requestChanged, Action<PanelSink> refresh, Func<int> frameRate,
        TimeProvider time, PanelRunnerOptions options, ILogger logger, PanelEncoding encoding = PanelEncoding.Raw, System.Text.Json.JsonElement familySettings = default)
    {
        _familySettings = familySettings;
        _driver = driver;
        _device = device;
        _identity = identity;
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

    /// <summary>
    /// The session's frame-rate ceiling as its pace keeps to it: its <see cref="PanelCapabilities.MaxFps"/> within 1 to
    /// 60 (a BeadaPanel's is <see cref="PanelPacer.CeilingOf"/>, else 60); null until the session has started.
    /// </summary>
    public int? MaxFps => _maxFps is var fps and > 0 ? fps : null;

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
        IPanelSession? session = null;
        try
        {
            // A refused JPEG start is kept at once, so the manager remembers it whatever the start comes to.
            var options = new PanelOpenOptions(_brightness, (int)Rotation, FormatOf(_encoding), _familySettings)
            {
                FormatRefused = refused =>
                {
                    var status = UsbStatusOf(refused);
                    _jpegRefused = new StrongBox<UsbStatus>(status);
                    _logger.Information("Panel {Panel}: the JPEG start wasn't taken ({Status}): raw frames, for this panel until the engine restarts", _label, status);
                },
            };
            var open = _driver.Open(_device, _identity, options, _stop.Token);
            if (open.Session is null)
            {
                end = new PanelRunEnd(true, UsbStatusOf(open.Status), open.Message);
                return;
            }
            session = open.Session;
            _jpeg = WrittenFormat(session.Capabilities) == PanelFrameFormat.Jpeg;
            end = Loop(session);
        }
        catch (OperationCanceledException)
        {
            end = new PanelRunEnd(false, UsbStatus.Ok, "stopped");
        }
        catch (InfoPanel.Panels.Hosted.PanelDriverException ex)
        {
            // Its driver's host ended, or its package is off or failed: the panel's reason is the driver's.
            _logger.Information("Panel {Panel}: {Why:l}", _label, ex.Message);
            end = new PanelRunEnd(true, UsbStatus.Io, ex.Message, Code: "driver");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Panel {Panel}: its thread failed", _label);
            end = new PanelRunEnd(true, UsbStatus.Io, ex.Message);
        }
        finally
        {
            // Whatever the stop comes to, nothing escapes the panel's thread (that would end the engine) and Ended completes.
            try
            {
                End(session);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Panel {Panel}: its stop failed", _label);
            }
            finally
            {
                _ended.TrySetResult(end);
            }
        }
    }

    /// <summary>The sink detached, then the session closed (<see cref="Close"/>), whatever the detach came to.</summary>
    private void End(IPanelSession? session)
    {
        Running = false;
        try
        {
            if (_attached is { } sink)
            {
                _attached = null;
                _detach(sink);
                sink.Retire();
            }
        }
        finally
        {
            if (session != null)
                Close(session);
        }
    }

    /// <summary>The session's screen-off, then its letting the device go, each tried whatever the other came to (a driver's failure in either is logged).</summary>
    private void Close(IPanelSession session)
    {
        try
        {
            // The screen-off's bound counts from the first ask to stop: the session is given what is left of it.
            var askedAt = Interlocked.Read(ref _stopAskedAt);
            session.Close(screenOff: true, askedAt >= 0 ? BeadaPanelSession.ScreenOffBound - _time.GetElapsedTime(askedAt) : BeadaPanelSession.ScreenOffBound);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Panel {Panel}: its screen off failed", _label);
        }
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Panel {Panel}: letting the panel go failed", _label);
        }
        _logger.Information("Panel {Panel}: stopped after {Writes} frames ({Fps:0.#} fps, {FrameTime:0.#} ms a frame)", _label, Writes, Fps, FrameTimeMs);
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
            made.PacedFps = _pacer.Paced; // the panel's pace, whichever profile it shows
        }
        if (!_attach(made))
            return null;
        _attached = made;
        return made;
    }

    private PanelRunEnd Loop(IPanelSession session)
    {
        var capabilities = session.Capabilities;
        if (WrittenFormat(capabilities) is not { } streamed)
            return Failed(UsbStatus.NotSupported, "the panel takes none of the pixel formats the engine writes");
        if (PanelFrameWriter.Problem(capabilities) is { } problem)
            return Failed(UsbStatus.NotSupported, $"its driver's capabilities can't be written: {problem}");
        using var writer = new PanelFrameWriter(capabilities, streamed, _logger, _label);
        // The session's own keep-alive interval when it gives one, else the runner's.
        var keepAlive = capabilities.KeepAliveEvery is { } every && every > TimeSpan.Zero ? every : _options.KeepAlive;
        var dimmedAt = _brightness;
        // The panel's own ceiling; none at 60 or more, which the pace never passes anyway.
        int? ceiling = capabilities.MaxFps < 60 ? Math.Max(1, capabilities.MaxFps) : null;
        _pacer = new PanelPacer(ceiling);
        _maxFps = ceiling ?? 60; // answered while the session runs (the frame rate is 1 to 60)
        var profileId = ProfileId;
        if (AttachSink(profileId) is not { } sink)
            return new PanelRunEnd(true, UsbStatus.Ok, $"profile {profileId} can't be rendered", FrameSinkClosedReason.ProfileDeleted);
        Running = true;
        var jpeg = streamed == PanelFrameFormat.Jpeg;
        _jpeg = jpeg;
        var (width, height) = (capabilities.Width, capabilities.Height);
        _logger.Information("Panel {Panel}: running, {Width} x {Height}, {Encoding:l}, profile {ProfileId}", _label, width, height,
            jpeg ? $"JPEG q{capabilities.JpegQuality}" : streamed == PanelFrameFormat.Rgb565Le ? "raw RGB565" : $"raw {streamed}", profileId);

        // A raw stream writes the transformed frame; a JPEG one encodes it once, and a re-send writes the same bytes.
        var written = false;
        long lastWrite = 0;
        var started = _time.GetTimestamp();
        var nextStats = _options.FirstStats;
        var lastPace = started;
        _fpsWindowStart = started;
        var loggedRev = long.MinValue;
        var pacedRate = _frameRate();
        var loggedFps = Math.Min(pacedRate, sink.PacedFps);
        long loggedPaceAt = -1;

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
                loggedRev = long.MinValue; // the new profile's first frame is logged, whatever its revision
                _logger.Information("Panel {Panel}: profile {ProfileId}", _label, profileId);
            }

            var brightness = _brightness;
            var status = UsbStatusOf(session.SetBrightness(brightness));
            if (status != UsbStatus.Ok)
                return Failed(status, "the brightness wasn't taken");
            if (_stop.IsCancellationRequested)
                break; // asked to stop during the brightness: no frame now, the screen-off next
            if (capabilities.DimInSoftware && brightness != dimmedAt)
            {
                // The engine dims this panel's frames: a new brightness needs a new frame, whatever the sink last had.
                dimmedAt = brightness;
                sink.Renew();
                _refresh(sink);
            }

            var frame = sink.Take();
            // How long the thread waited for this frame since its last write returned: an unloaded write needs one.
            var idleMs = written ? _time.GetElapsedTime(lastWrite).TotalMilliseconds : double.PositiveInfinity;
            long? encodeStarted = null;
            var repeat = frame == null;
            if (frame != null)
            {
                long frameId, frameRev;
                try
                {
                    Prepare(frame, writer, dimmedAt);
                    (frameId, frameRev) = (frame.FrameId, frame.Rev);
                }
                finally
                {
                    sink.Return(frame);
                }
                if (jpeg)
                    encodeStarted = _time.GetTimestamp();
                var encoded = writer.Encode();
                if (jpeg)
                {
                    Interlocked.Increment(ref _encodes);
                    _options.AfterEncode?.Invoke();
                }
                if (!encoded)
                {
                    // Over the panel's cap even at its floor: not sent, the panel keeps its last frame; a keep-alive due
                    // still goes, with that frame, however many such frames come.
                    if (!(written && keepAlive != Timeout.InfiniteTimeSpan && _time.GetElapsedTime(lastWrite) >= keepAlive))
                        continue;
                    repeat = true;
                    encodeStarted = null;
                }
                else
                    (_frameId, _frameRev) = (frameId, frameRev);
            }
            else if (!(written && keepAlive != Timeout.InfiniteTimeSpan && _time.GetElapsedTime(lastWrite) >= keepAlive))
            {
                // Until a frame comes, a stop or a brightness change, or the keep-alive is due (none before the first write).
                WaitForWork(written && keepAlive != Timeout.InfiniteTimeSpan
                    ? keepAlive - _time.GetElapsedTime(lastWrite)
                    : Timeout.InfiniteTimeSpan);
                continue;
            }

            if (_stop.IsCancellationRequested)
                break;
            // A JPEG frame's time counts its encode too: the panel's thread spends both on it.
            var writeStarted = _time.GetTimestamp();
            var before = encodeStarted ?? writeStarted;
            var write = session.WriteFrame(new PanelFrameView(writer.Frame, streamed, _frameId, repeat: repeat), _stop.Token);
            status = UsbStatusOf(write.Status);
            if (status != UsbStatus.Ok)
                return Failed(status, "a frame wasn't taken");
            lastWrite = _time.GetTimestamp();
            written = true;
            Interlocked.Exchange(ref _lastFrameId, _frameId);
            var frameMs = _time.GetElapsedTime(before, lastWrite).TotalMilliseconds;
            Count(frameMs, lastWrite);
            // The write's own time is the device's, as the driver measured its transfer; the frame's is the thread's.
            _pacer.Record(write.DeviceTime.TotalMilliseconds, frameMs, idleMs, lastWrite, _time);
            if (_frameRev != loggedRev)
            {
                loggedRev = _frameRev;
                _logger.Information("Panel {Panel}: wrote rev {Rev}, encode+write {Ms:0.#} ms", _label, _frameRev, frameMs);
            }

            if (_time.GetElapsedTime(lastPace) >= TimeSpan.FromSeconds(1))
            {
                lastPace = _time.GetTimestamp();
                var frameRate = _frameRate();
                if (frameRate != pacedRate)
                {
                    // A new setting: what was learned at the old one is forgotten.
                    pacedRate = frameRate;
                    _pacer = new PanelPacer(ceiling);
                }
                var paced = _pacer.Evaluate(FrameTimeMs, Math.Min(frameRate, sink.PacedFps));
                if (paced != sink.PacedFps)
                {
                    sink.PacedFps = paced;
                    _requestChanged(sink);
                }
                var fps = Math.Min(frameRate, paced);
                if (Math.Abs(fps - loggedFps) > 0.2 * loggedFps && (loggedPaceAt < 0 || _time.GetElapsedTime(loggedPaceAt) >= PaceLogEvery))
                {
                    loggedFps = fps;
                    loggedPaceAt = lastPace;
                    _logger.Information("Panel {Panel}: pacing {Fps} fps (writes {Recent:0.#} ms, unloaded {Base:0.#} ms)", _label, fps, _pacer.RecentMs, _pacer.UnloadedMs);
                }
            }

            if (_time.GetElapsedTime(started) >= nextStats)
            {
                nextStats += _options.StatsEvery;
                _logger.Information("Panel {Panel}: {Fps:0.#} fps, {FrameTime:0.#} ms a frame", _label, Fps, FrameTimeMs);
            }

            // The pace is kept here, at every write: the render loop may deliver a frame every one of its ticks, faster than
            // the pace when that is a little under them. The next write begins no sooner than one interval of the lower of
            // the setting and the pace after this one began, with the newest frame by then.
            var pace = Math.Clamp(Math.Min(Math.Min(_frameRate(), sink.PacedFps), ceiling ?? 60), 1, 60);
            PaceWait(TimeSpan.FromTicks((TimeSpan.TicksPerSecond + pace - 1) / pace) - _time.GetElapsedTime(writeStarted));
        }

        return _sinkClosed?.Value is { } closed
            ? new PanelRunEnd(true, UsbStatus.Ok, $"profile {ProfileId} closed ({closed})", closed)
            : new PanelRunEnd(false, UsbStatus.Ok, "stopped");
    }

    /// <summary>The format the engine writes the session in: the first of its formats the engine produces (every format the SDK names); null when none is.</summary>
    private static PanelFrameFormat? WrittenFormat(PanelCapabilities capabilities)
    {
        foreach (var format in capabilities.Formats)
            if (Enum.IsDefined(format))
                return format;
        return null;
    }

    /// <summary>The format asked of the driver for <paramref name="encoding"/>: none (its own choice), JPEG, or RGB565.</summary>
    private static PanelFrameFormat? FormatOf(PanelEncoding encoding) => encoding switch
    {
        PanelEncoding.Jpeg => PanelFrameFormat.Jpeg,
        PanelEncoding.Raw => PanelFrameFormat.Rgb565Le,
        _ => null,
    };

    internal static UsbStatus UsbStatusOf(PanelDeviceStatus status) => status switch
    {
        PanelDeviceStatus.Ok => UsbStatus.Ok,
        PanelDeviceStatus.Busy => UsbStatus.Busy,
        PanelDeviceStatus.Access => UsbStatus.Access,
        PanelDeviceStatus.Gone => UsbStatus.NoDevice,
        PanelDeviceStatus.NotSupported => UsbStatus.NotSupported,
        PanelDeviceStatus.Timeout => UsbStatus.Timeout,
        _ => UsbStatus.Io,
    };

    private PanelRunEnd Failed(UsbStatus status, string what)
    {
        _logger.Information("Panel {Panel}: {What} ({Status})", _label, what, status);
        return new PanelRunEnd(true, status, $"{what} ({status})");
    }

    /// <summary>Waits <paramref name="due"/> (through the <see cref="TimeProvider"/>), or until a stop; a new frame doesn't end it.</summary>
    private void PaceWait(TimeSpan due)
    {
        if (due <= TimeSpan.Zero || _stop.IsCancellationRequested)
            return;
        try
        {
            // Rounded up: a delay counts whole milliseconds, and one cut short would let the pace be exceeded.
            Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(due.TotalMilliseconds)), _time, _stop.Token).Wait();
        }
        catch (AggregateException) when (_stop.IsCancellationRequested)
        {
        }
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

    /// <summary>The copied frame (Rgba8888, premultiplied) prepared by the session's writer (turned, dimmed, overlaid, cropped), at its rotation.</summary>
    private static unsafe void Prepare(PanelFrame frame, PanelFrameWriter writer, int brightness)
    {
        fixed (byte* pixels = frame.Pixels)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul), (IntPtr)pixels, frame.RowBytes);
            writer.Prepare(pixmap, frame.Rotation, brightness);
        }
    }
}
