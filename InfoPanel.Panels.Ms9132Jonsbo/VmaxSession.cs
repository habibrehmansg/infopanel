using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// A running VMAX 4.6" on its MS9132, as 1.4 drove it (VmaxUsbDevice.cs and VmaxPanelDeviceTask.cs): BGR888 frames at
/// 320 × 960 in 64 KiB bulk writes behind a fixed header, at most 30 a second; after each frame the status polled at
/// most every 500 ms, and the screen switch set to the family setting Screen on once frames flow (and whenever it
/// differs from what was set). The engine dims the frame (1.4 applied the brightness in its conversion). A control report
/// that times out or fails ends the session (the engine's HID seam closes the collection's stream when a report times
/// out). The stop sends nothing, as 1.4's dispose of the device sent nothing.
/// </summary>
internal sealed class VmaxSession : IPanelSession
{
    /// <summary>1.4 sent at most 30 frames a second (VmaxPanelDeviceTask.cs:143, <c>0b43898</c>).</summary>
    public const int MaxFps = 30;

    private readonly Ms9132Link _link;
    private readonly IPanelUsb _usb;
    private readonly byte _endpoint;
    private readonly int _pixels;
    private readonly byte[] _frame;
    private readonly bool _screenOn;
    private readonly TimeProvider _time;
    private readonly TimeSpan _writeBudget;
    private long? _lastPolled;
    private bool? _screenSet;
    private bool _closed;

    private VmaxSession(Ms9132Link link, IPanelUsb usb, int width, int height, bool screenOn, TimeProvider time, TimeSpan writeBudget)
    {
        _link = link;
        _usb = usb;
        _endpoint = Ms9132Protocol.WriteEndpoint(usb.Endpoints);
        _pixels = width * height * 3;
        _frame = new byte[Ms9132Protocol.FrameLength(width, height)];
        _screenOn = screenOn;
        _time = time;
        _writeBudget = writeBudget;
        Capabilities = new PanelCapabilities(width, height, [PanelFrameFormat.Bgr888], MaxFps) { DimInSoftware = true };
    }

    public PanelCapabilities Capabilities { get; }

    /// <summary>The output endpoint frames go to (tests).</summary>
    internal byte Endpoint => _endpoint;

    /// <summary>How long the video interface's open may take.</summary>
    internal const int OpenTimeoutMs = 5000;

    /// <summary>
    /// 1.4's start (VmaxUsbDevice.cs:105-151): the video interface claimed, then each HID collection in turn until one takes
    /// the streaming start (its block with its pauses, 42 pulses 2 ms apart, the display enable, 25 ms, the status, 25 ms).
    /// </summary>
    public static PanelOpen Start(IPanelDevices devices, PanelCandidate device, PanelManifestModel model, bool screenOn, TimeProvider time,
        Action<TimeSpan, CancellationToken> wait, TimeSpan writeBudget, CancellationToken stop)
    {
        var status = devices.OpenUsb(device, Ms9132Protocol.VideoInterface, OpenTimeoutMs, out var usb, stop);
        if (usb == null)
            return new(null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status,
                "the MS9132's video interface (3) couldn't be opened: on Windows it needs the WinUSB driver");
        Ms9132Link? link = null;
        try
        {
            status = devices.ListHid(device, new PanelHidFilter(), out var collections);
            if (status == PanelDeviceStatus.Ok && collections.Count == 0)
                status = PanelDeviceStatus.NotSupported;
            if (status == PanelDeviceStatus.Ok)
            {
                status = PanelDeviceStatus.Io;
                foreach (var collection in collections)
                {
                    var opened = devices.OpenHid(device, collection, Ms9132Protocol.HidTimeoutMs, out var hid, stop);
                    if (hid == null)
                    {
                        status = opened == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : opened;
                        if (status == PanelDeviceStatus.Gone)
                            break;
                        continue;
                    }
                    var tried = new Ms9132Link(hid, wait);
                    try
                    {
                        status = StreamingStart(tried, stop);
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
                    hid.Dispose(); // 1.4: "HID streaming init failed", the next one
                    if (status == PanelDeviceStatus.Gone)
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            usb.Dispose();
            throw;
        }
        if (link == null)
        {
            usb.Dispose();
            return new(null, status, "no HID collection took the VMAX's streaming start");
        }
        return new(new VmaxSession(link, usb, model.Width, model.Height, screenOn, time, writeBudget), PanelDeviceStatus.Ok, "");
    }

    // VmaxUsbDevice.cs:217-241
    private static PanelDeviceStatus StreamingStart(Ms9132Link link, CancellationToken stop)
    {
        foreach (var report in VmaxMs9132.Startup)
        {
            var status = link.Send(report, Ms9132Protocol.HidTimeoutMs, VmaxMs9132.DelayMs(report), stop);
            if (status != PanelDeviceStatus.Ok)
                return status;
        }
        for (var i = 0; i < VmaxMs9132.Pulses; i++)
        {
            var status = link.Send(VmaxMs9132.Pulse, Ms9132Protocol.HidTimeoutMs, VmaxMs9132.PulseGapMs, stop);
            if (status != PanelDeviceStatus.Ok)
                return status;
        }
        var enabled = link.Send(VmaxMs9132.DisplayEnable, Ms9132Protocol.HidTimeoutMs, VmaxMs9132.StreamingDelayMs, stop);
        if (enabled != PanelDeviceStatus.Ok)
            return enabled;
        return link.Send(VmaxMs9132.Status, Ms9132Protocol.HidTimeoutMs, VmaxMs9132.StreamingDelayMs, stop);
    }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        if (frame.Bytes.Length < _pixels)
            return new(PanelDeviceStatus.NotSupported, TimeSpan.Zero); // not the session's size: never sent
        VmaxMs9132.Frame(frame.Bytes[.._pixels], _frame);

        // The whole frame within the write budget: each write is given 1.4's 1 s or what is left, whichever is less.
        var started = Stopwatch.GetTimestamp();
        int Left() => (int)Math.Clamp(Math.Floor((_writeBudget - Stopwatch.GetElapsedTime(started)).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);
        for (var offset = 0; offset < _frame.Length; offset += Ms9132Protocol.ChunkSize)
        {
            var length = Math.Min(Ms9132Protocol.ChunkSize, _frame.Length - offset);
            var left = Left();
            var status = left < 1
                ? PanelDeviceStatus.Timeout
                : _usb.BulkWrite(_endpoint, _frame.AsSpan(offset, length), Math.Min(VmaxMs9132.WriteTimeoutMs, left), cancellationToken: stop);
            if (status != PanelDeviceStatus.Ok)
                return new(status, Stopwatch.GetElapsedTime(started));
        }
        var deviceTime = Stopwatch.GetElapsedTime(started);

        // The status at most every 500 ms (VmaxUsbDevice.cs:375-395). 1.4 let a failed poll pass; here one that times out
        // or fails ends the session (the engine opens it again): the engine's HID seam closes the stream a report times
        // out on, so the Screen on that follows could never reach the panel. A frame that left no time for it skips it.
        var now = _time.GetTimestamp();
        if ((_lastPolled is not { } last || _time.GetElapsedTime(last, now) >= VmaxMs9132.StatusEvery) && Left() >= 1)
        {
            _lastPolled = now;
            var polled = _link.SendWithin(VmaxMs9132.Status, Left, 0, stop);
            if (JonsboMs9132Session.EndsTheSession(polled))
                return new(polled, deviceTime);
        }
        // The screen switch once frames flow, set again until it takes (VmaxUsbDevice.cs:341-365): a report refused
        // otherwise is let pass, as 1.4 let it; one that times out or fails ends the session, as the poll's does.
        if (_screenSet != _screenOn && Left() >= 1)
        {
            var all = true;
            foreach (var report in VmaxMs9132.ScreenSwitch(_screenOn))
            {
                var status = _link.SendWithin(report, Left, VmaxMs9132.DelayMs(report), stop);
                if (JonsboMs9132Session.EndsTheSession(status))
                    return new(status, deviceTime);
                if (status != PanelDeviceStatus.Ok)
                {
                    all = false;
                    break;
                }
            }
            if (all)
                _screenSet = _screenOn;
        }
        return new(PanelDeviceStatus.Ok, deviceTime);
    }

    /// <summary>Nothing to send: the engine dims the frame (<see cref="PanelCapabilities.DimInSoftware"/>), as 1.4 dimmed it in its conversion.</summary>
    public PanelDeviceStatus SetBrightness(int percent) => PanelDeviceStatus.Ok;

    /// <summary>Sends nothing, as 1.4's dispose of the device sent nothing; lets the HID collection and the video interface go.</summary>
    public void Close(bool screenOff, TimeSpan budget) => Dispose();

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
