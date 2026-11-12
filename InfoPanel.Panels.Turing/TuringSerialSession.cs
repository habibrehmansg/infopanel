using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// A running serial Turing panel (revision A, C or E) over its port, as 1.4's <c>Services/TuringPanelSerialTask.cs</c>
/// drove it: the screen opened (C and E: Hello), on, at its brightness; the first frame whole, then only the sectors that
/// changed since what the panel was sent (kept here, in the driver), each at its place, and a whole frame again when more
/// changed than the revision's cap or when the panel asked for the last sector again; the brightness between frames;
/// the screen off on stop.
/// <para>
/// A frame's write starts pieces for <see cref="Budget"/> and returns: a hosted package's write must answer within the
/// engine's deadline (5 s), and a stop asked while it is under way waits for it, so it must end soon enough for the
/// screen-off to follow within the stop's bound (3 s from the ask). A whole frame takes longer (revision A's 300 KB
/// about 27 s at 115200 baud; C's and E's blocks about a second each, their answers' reads: the 5"'s 60, the 8.8"'s
/// 144, the 2.1"'s 36). What is left goes with the next write, a new frame's or the engine's keep-alive (a second
/// later), in the newest frame's pixels: a whole frame thus arrives over many writes, and the sectors its parts sent
/// earlier missed of a newer frame follow. A piece already started is finished, so the panel only ever gets whole
/// commands. A keep-alive with nothing left to send sends nothing (the panel keeps its picture).
/// </para>
/// </summary>
internal sealed class TuringSerialSession : IPanelSession
{
    /// <summary>1.4 paced the serial panels by its Target FPS, at most 30 (its slider's top).</summary>
    public const int MaxFps = 30;

    /// <summary>
    /// How long a frame's write starts new pieces for. A write then ends within the budget and its longest piece: a
    /// band of revision A, 10 KB, about 0.9 s at 115200 baud; a block of C or E, at most 26 KB, about 30 ms over a USB
    /// serial port at 1 MB/s (full speed's), its answer and the read that brings nothing 1 s: 2.2 s at most, within a
    /// hosted write's 5 s. A stop asked as a write begins waits for it (the host's close takes the session's turn; the
    /// write doesn't see the stop), and the screen-off after it is one command (A's 6 bytes, C's and E's 250, a few
    /// milliseconds): about 2.2 s from the ask, which leaves some 0.8 s of the stop's 3 s to the engine and its host.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(1.2);

    private readonly SerialRevision _revision;
    private readonly IPanelSerial _port;
    private readonly TuringSerialScreen _screen;
    private readonly Func<long> _milliseconds;
    private readonly int _frameBytes;
    private readonly Queue<(Sector Piece, int Group)> _pieces = new();
    private byte[]? _frame;   // the newest frame
    private byte[]? _shown;   // what the panel was sent: each piece copied in once it went
    private bool _canDisplayPartial;
    private bool _groupResult;
    private bool _on;
    private bool _closed;

    public TuringSerialSession(SerialRevision revision, IPanelSerial port, int width, int height, Func<long> milliseconds)
    {
        _revision = revision;
        _port = port;
        _screen = TuringSerialScreen.Create(revision, port);
        _milliseconds = milliseconds;
        _frameBytes = width * height * revision.BytesPerPixel;
        Capabilities = new PanelCapabilities(width, height, [revision.Format], MaxFps, Brightness: new PanelRange(0, 100));
    }

    public PanelCapabilities Capabilities { get; }

    /// <summary>
    /// <c>ScreenFactory.Create</c> (the port's input dropped; C and E: Hello and its answer), then <c>ScreenOn</c> and
    /// <c>SetBrightness</c> (<c>TuringPanelSerialTask.cs:101-115</c>).
    /// </summary>
    public PanelDeviceStatus Start(int brightness, CancellationToken stop, out string why)
    {
        var status = _screen.Open(stop, out why);
        if (status != PanelDeviceStatus.Ok)
            return status;
        _on = true;
        status = _screen.ScreenOn(stop);
        if (status != PanelDeviceStatus.Ok)
        {
            why = $"the panel didn't take ScreenOn ({status})";
            return status;
        }
        status = _screen.SetBrightness(Math.Clamp(brightness, 0, 100), stop);
        if (status != PanelDeviceStatus.Ok)
            why = $"the panel didn't take its brightness ({status})";
        return status;
    }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        if (frame.Bytes.Length != _frameBytes)
            return new(PanelDeviceStatus.NotSupported, TimeSpan.Zero);
        if (!frame.Repeat || _frame == null)
            _frame = frame.Bytes.ToArray();
        if (_pieces.Count == 0)
            Plan();
        if (_pieces.Count == 0)
            return new(PanelDeviceStatus.Ok, TimeSpan.Zero); // the panel shows the frame: nothing to send

        var width = Capabilities.Width;
        var since = _milliseconds();
        var started = Stopwatch.GetTimestamp();
        var status = PanelDeviceStatus.Ok;
        do
        {
            var (piece, group) = _pieces.Dequeue();
            status = _screen.Display(_frame, width, piece, stop, out var result);
            if (status != PanelDeviceStatus.Ok)
                break;
            Copy(_frame, _shown!, piece);
            if (!result) // DisplayBitmap stopped at a block the panel asked for again: the rest of its area isn't sent
                while (_pieces.TryPeek(out var next) && next.Group == group)
                    _pieces.Dequeue();
            if (!_pieces.TryPeek(out var following) || following.Group != group)
                _groupResult = result; // DisplayBuffer's answer for the area
            if (_pieces.Count == 0)
                _canDisplayPartial = _groupResult; // the last area's, as 1.4 kept it (:133-137,151-154)
        }
        while (_pieces.Count > 0 && _milliseconds() - since < Budget.TotalMilliseconds && !stop.IsCancellationRequested);
        return new(status, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// What the newest frame needs (<c>TuringPanelSerialTask.cs:133-157</c>): the whole frame when the panel was never
    /// sent one or asked for one, or when more sectors changed than the revision's cap; else the sectors that changed
    /// since what it was sent, each an area of its own; or nothing.
    /// </summary>
    private void Plan()
    {
        var width = Capabilities.Width;
        var height = Capabilities.Height;
        var whole = new Sector(0, 0, width, height);
        if (_shown == null || !_canDisplayPartial)
        {
            _shown ??= new byte[_frameBytes];
            Queue(whole, true, 0);
            return;
        }
        var sectors = SectorDiff.Changed(_shown, _frame!, width, height, _revision.BytesPerPixel,
            _revision.SectorWidth, _revision.SectorHeight, _revision.MaxSectorWidth, _revision.MaxSectorHeight);
        if (sectors.Count > _revision.MaxSectors)
        {
            Queue(whole, true, 0);
            return;
        }
        for (var i = 0; i < sectors.Count; i++)
            Queue(sectors[i], false, i);
    }

    private void Queue(Sector area, bool whole, int group)
    {
        foreach (var piece in _screen.Pieces(area, whole))
            _pieces.Enqueue((piece, group));
    }

    private void Copy(byte[] from, byte[] to, Sector piece)
    {
        var bytesPerPixel = _revision.BytesPerPixel;
        var rowBytes = Capabilities.Width * bytesPerPixel;
        for (var y = piece.Top; y < piece.Bottom; y++)
        {
            var offset = (y * rowBytes) + (piece.Left * bytesPerPixel);
            Array.Copy(from, offset, to, offset, piece.Width * bytesPerPixel);
        }
    }

    /// <summary>The brightness, 0–100, between frames (<c>TuringPanelSerialTask.cs:128-132</c>).</summary>
    public PanelDeviceStatus SetBrightness(int percent) => _screen.SetBrightness(Math.Clamp(percent, 0, 100), CancellationToken.None);

    /// <summary>
    /// With <paramref name="screenOff"/>, <c>ScreenOff</c> (<c>TuringPanelSerialTask.cs:194</c>; A's command 108, C's and
    /// E's brightness 0) within <paramref name="budget"/>, none once the device is gone or before the screen was opened;
    /// then the port let go.
    /// </summary>
    public void Close(bool screenOff, TimeSpan budget)
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            if (!screenOff || !_on || _screen.Gone)
                return;
            var clock = Stopwatch.StartNew();
            _screen.ScreenOff(() => (int)Math.Clamp(Math.Floor((budget - clock.Elapsed).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs));
        }
        finally
        {
            _port.Dispose();
        }
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _port.Dispose();
    }
}
