using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// A running Jonsbo DS339 (or a panel tried as one) on its MS9132, as 1.4 drove it (JonsboMs9132Device.cs and
/// JonsboPanelDeviceTask.cs:295-403): BGR888 frames, each in 64 KiB bulk writes with its header and trailer, the status
/// polled after each, the output turned on after the second; the engine dims the frame (1.4 dimmed in its conversion).
/// The stop powers the panel off, as 1.4's dispose did, unless a transfer failed: then it stays powered, as 1.4's retry
/// left it, so a failing panel isn't seen to flicker off and on. A control report (the status, the output's enable) that
/// times out or fails ends the session too, the panel left powered: the engine's HID seam closes the collection's stream
/// when a report times out, so nothing more would reach the panel through it.
/// </summary>
internal sealed class JonsboMs9132Session : IPanelSession
{
    /// <summary>1.4's frame-rate slider's top for a Jonsbo panel (Views/Pages/UsbPanelsPage.xaml's 60).</summary>
    public const int MaxFps = 60;

    private readonly Ms9132Link _link;
    private readonly IPanelUsb _usb;
    private readonly byte _endpoint;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _frame;
    private readonly TimeSpan _writeBudget;
    private int _framesSent;
    private bool _gone;
    private bool _failed;
    private bool _closed;

    private JonsboMs9132Session(Ms9132Link link, IPanelUsb usb, int width, int height, TimeSpan writeBudget)
    {
        _link = link;
        _usb = usb;
        _endpoint = Ms9132Protocol.WriteEndpoint(usb.Endpoints);
        _width = width;
        _height = height;
        _frame = new byte[Ms9132Protocol.FrameLength(width, height)];
        _writeBudget = writeBudget;
        Capabilities = new PanelCapabilities(width, height, [PanelFrameFormat.Bgr888], MaxFps) { DimInSoftware = true };
    }

    public PanelCapabilities Capabilities { get; }

    /// <summary>The output endpoint frames go to (tests).</summary>
    internal byte Endpoint => _endpoint;

    /// <summary>
    /// 1.4's start (JonsboMs9132Device.cs:134-209, JonsboPanelDeviceTask.cs:304-345): each HID collection in turn until one
    /// takes the startup block, the video interface claimed, the panel's status asked, the mode set for the EDID's size
    /// when 1.4's mode table has it (else <paramref name="model"/>'s), then 1.4's pause before the first frame.
    /// </summary>
    public static PanelOpen Start(IPanelDevices devices, PanelCandidate device, PanelManifestModel model, Action<TimeSpan, CancellationToken> wait, TimeSpan writeBudget, CancellationToken stop)
    {
        var status = devices.ListHid(device, new PanelHidFilter(), out var collections);
        if (status != PanelDeviceStatus.Ok)
            return new(null, status, "its HID collections couldn't be listed");
        if (collections.Count == 0)
            return new(null, PanelDeviceStatus.NotSupported, "it has no HID collection, so no MS9132 control interface");

        Ms9132Link? link = null;
        var edid = new byte[Ms9132Protocol.EdidLength];
        status = PanelDeviceStatus.Io;
        foreach (var collection in collections)
        {
            var opened = devices.OpenHid(device, collection, Ms9132Protocol.HidTimeoutMs, out var hid, stop);
            if (hid == null)
            {
                status = opened == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : opened;
                if (status == PanelDeviceStatus.Gone)
                    break;
                continue; // 1.4: "Cannot open HID interface", the next one
            }
            var tried = new Ms9132Link(hid, wait);
            try
            {
                Array.Clear(edid);
                status = Startup(tried, edid, stop);
            }
            catch (OperationCanceledException)
            {
                hid.Dispose();
                throw;
            }
            if (status == PanelDeviceStatus.Ok)
            {
                link = tried;
                break;
            }
            hid.Dispose(); // 1.4: "Startup block rejected", the next one
            if (status == PanelDeviceStatus.Gone)
                break;
        }
        if (link == null)
            return new(null, status, "no HID collection took the MS9132's startup block");

        IPanelUsb? usb;
        try
        {
            status = devices.OpenUsb(device, Ms9132Protocol.VideoInterface, OpenTimeoutMs, out usb, stop);
        }
        catch (OperationCanceledException)
        {
            PowerOffAndLetGo(link, null);
            throw;
        }
        if (usb == null)
        {
            if (status == PanelDeviceStatus.Gone)
            {
                link.Hid.Dispose(); // the device went: nothing more is sent to it
                return new(null, status, "it went away");
            }
            // 1.4's open failed here and its dispose powered the panel off.
            PowerOffAndLetGo(link, null);
            return new(null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status,
                "the MS9132's video interface (3) couldn't be opened: on Windows it needs the WinUSB driver");
        }

        // The EDID's size when 1.4's mode table has it, else the model's (JonsboPanelDeviceTask.cs:324-345).
        var (width, height) = (model.Width, model.Height);
        var vic = Ms9132JonsboManifest.VicOf(model) ?? JonsboMs9132.VicOf(width, height) ?? 0;
        if (Edid.Parse(edid) is { HasSize: true } read && JonsboMs9132.VicOf(read.Width, read.Height) is { } edidVic)
            (width, height, vic) = (read.Width, read.Height, edidVic);

        var session = new JonsboMs9132Session(link, usb, width, height, writeBudget);
        try
        {
            // 1.4 asked whether a panel is connected and went on either way (JonsboPanelDeviceTask.cs:314-318). An ask that
            // timed out or failed ends the start here: the engine's HID seam closes the stream a report times out on, so
            // nothing more could reach the panel (the panel left powered, as after a failed mode set).
            status = link.Send(JonsboMs9132.PanelStatus, Ms9132Protocol.HidTimeoutMs, JonsboMs9132.DelayMs(JonsboMs9132.PanelStatus), stop);
            if (EndsTheSession(status))
            {
                session.Failed(status);
                session.Close(screenOff: true, StartCloseBudget);
                return new(null, status, status == PanelDeviceStatus.Gone ? "it went away" : $"the MS9132 didn't answer its status ({status})");
            }
            foreach (var report in JonsboMs9132.SetMode(width, height, vic))
            {
                status = link.Send(report, Ms9132Protocol.HidTimeoutMs, JonsboMs9132.DelayMs(report), stop);
                if (status != PanelDeviceStatus.Ok)
                {
                    // 1.4's retry left the panel powered after a failed mode set.
                    session.Failed(status);
                    session.Close(screenOff: true, StartCloseBudget);
                    return new(null, status, "the MS9132 didn't take its mode set");
                }
            }
            wait(JonsboMs9132.FirstFrameDelay, stop);
            return new(session, PanelDeviceStatus.Ok, "");
        }
        catch (OperationCanceledException)
        {
            session.Close(screenOff: true, StartCloseBudget);
            throw;
        }
    }

    /// <summary>How long the video interface's open may take.</summary>
    internal const int OpenTimeoutMs = 5000;

    /// <summary>The time a failed or cancelled start gives its power-off: the engine's bound for a stop.</summary>
    internal static readonly TimeSpan StartCloseBudget = TimeSpan.FromSeconds(3);

    // The startup block, its EDID reads' bytes kept (JonsboMs9132Device.cs:211-229).
    private static PanelDeviceStatus Startup(Ms9132Link link, byte[] edid, CancellationToken stop)
    {
        foreach (var report in JonsboMs9132.Startup)
        {
            var status = link.Send(report, Ms9132Protocol.HidTimeoutMs, JonsboMs9132.DelayMs(report), stop);
            if (status != PanelDeviceStatus.Ok)
                return status;
            if (Ms9132Protocol.IsEdidRead(report))
                Ms9132Protocol.TakeEdid(report, link.Reply, edid);
        }
        return PanelDeviceStatus.Ok;
    }

    private static void PowerOffAndLetGo(Ms9132Link link, IPanelUsb? usb)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            link.SendWithin(JonsboMs9132.PowerOff, () => (int)Math.Clamp(Math.Floor((StartCloseBudget - clock.Elapsed).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs),
                JonsboMs9132.DelayMs(JonsboMs9132.PowerOff), wholePause: true);
        }
        finally
        {
            link.Hid.Dispose();
            usb?.Dispose();
        }
    }

    /// <summary>
    /// Whether a control report's status ends the session: the device gone, a report timed out (the engine's HID seam then
    /// closes the collection's stream), or failed (a closed stream, or a reply that isn't the whole report).
    /// </summary>
    internal static bool EndsTheSession(PanelDeviceStatus status) =>
        status is PanelDeviceStatus.Gone or PanelDeviceStatus.Timeout or PanelDeviceStatus.Io;

    private void Failed(PanelDeviceStatus status)
    {
        if (status == PanelDeviceStatus.Gone)
            _gone = true;
        else
            _failed = true;
    }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        var pixels = _width * _height * 3;
        if (frame.Bytes.Length < pixels)
            return new(PanelDeviceStatus.NotSupported, TimeSpan.Zero); // not the session's size: never sent
        JonsboMs9132.Frame(frame.Bytes, _width, _height, _frame);

        // The whole frame within the write budget: each write is given 1.4's 5 s or what is left, whichever is less.
        var started = Stopwatch.GetTimestamp();
        int Left() => (int)Math.Clamp(Math.Floor((_writeBudget - Stopwatch.GetElapsedTime(started)).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);
        for (var offset = 0; offset < _frame.Length; offset += Ms9132Protocol.ChunkSize)
        {
            var length = Math.Min(Ms9132Protocol.ChunkSize, _frame.Length - offset);
            var left = Left();
            var status = left < 1
                ? PanelDeviceStatus.Timeout
                : _usb.BulkWrite(_endpoint, _frame.AsSpan(offset, length), Math.Min(JonsboMs9132.WriteTimeoutMs, left), cancellationToken: stop);
            if (status != PanelDeviceStatus.Ok)
            {
                // 1.4 aborted the pipe so the next frame began at a header (the seam has no abort: the session ends,
                // and the next one claims the interface afresh) and left the panel powered.
                Failed(status);
                return new(status, Stopwatch.GetElapsedTime(started));
            }
        }
        var deviceTime = Stopwatch.GetElapsedTime(started);
        _framesSent++;

        // 1.4 polled the status after each frame and let a failed poll pass (JonsboMs9132Device.cs:316-320). Here a poll
        // that times out or fails ends the session (the engine opens it again): the engine's HID seam closes the stream a
        // report times out on, which would leave the session without its status, output and power-off. A frame that left
        // no time for the poll skips it.
        if (Left() >= 1)
        {
            var polled = _link.SendWithin(JonsboMs9132.PanelStatus, Left, JonsboMs9132.DelayMs(JonsboMs9132.PanelStatus), stop);
            if (EndsTheSession(polled))
            {
                Failed(polled);
                return new(polled, deviceTime);
            }
        }
        // The output on once two frames are on the wire, as the vendor's app does (JonsboPanelDeviceTask.cs:390-394).
        if (_framesSent == JonsboMs9132.EnableVideoAfter)
        {
            foreach (var report in JonsboMs9132.EnableVideo)
            {
                var status = _link.SendWithin(report, Left, JonsboMs9132.DelayMs(report), stop);
                if (status != PanelDeviceStatus.Ok)
                {
                    Failed(status);
                    return new(status, deviceTime);
                }
            }
        }
        return new(PanelDeviceStatus.Ok, deviceTime);
    }

    /// <summary>Nothing to send: the engine dims the frame (<see cref="PanelCapabilities.DimInSoftware"/>); 1.4 had no brightness command for the MS9132.</summary>
    public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

    /// <summary>
    /// With <paramref name="screenOff"/>, 1.4's power-off (<c>A6 07 00</c>, set and got back, then its whole pause), each
    /// call within what is left of <paramref name="budget"/>, the get's less the pause; none once the device is gone, and none after a failed transfer
    /// (1.4's retry left the panel powered). Then the HID collection and the video interface let go.
    /// </summary>
    public void Close(bool screenOff, TimeSpan budget)
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            if (screenOff && !_gone && !_failed)
            {
                var clock = Stopwatch.StartNew();
                _link.SendWithin(JonsboMs9132.PowerOff,
                    () => (int)Math.Clamp(Math.Floor((budget - clock.Elapsed).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs),
                    JonsboMs9132.DelayMs(JonsboMs9132.PowerOff), wholePause: true);
            }
        }
        finally
        {
            try
            {
                _link.Hid.Dispose();
            }
            finally
            {
                _usb.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            _link.Hid.Dispose();
        }
        finally
        {
            _usb.Dispose();
        }
    }
}
