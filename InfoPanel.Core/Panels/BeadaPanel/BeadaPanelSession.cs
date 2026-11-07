using System;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Panels.Usb;
using Serilog;

namespace InfoPanel.Panels.BeadaPanel;

/// <summary>What <see cref="BeadaPanelSession.Start"/> came to: a running session, or the status and why not.</summary>
public sealed record BeadaPanelStart(BeadaPanelSession? Session, UsbStatus Status, string Message);

/// <summary>What <see cref="BeadaPanelSession.Identify"/> read of a panel.</summary>
public sealed record BeadaPanelIdentity(UsbStatus Status, BeadaPanelInfo? Info, BeadaPanelInfoError Error, byte ModelCode, string? UsbSerial);

/// <summary>
/// One BeadaPanel stream, as InfoPanel 1.4 runs it: the start sequence, frames, brightness between frames, and the
/// screen-off stop. Its calls block (bulk transfers, and the start's waits through the <see cref="TimeProvider"/>); one
/// thread drives it.
/// </summary>
public sealed class BeadaPanelSession : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<BeadaPanelSession>();

    private readonly IUsbDevice _device;
    private byte _backlight;
    private bool _stopped;
    private bool _disposed;

    private BeadaPanelSession(IUsbDevice device, BeadaPanelInfo info, byte backlight)
    {
        _device = device;
        Info = info;
        _backlight = backlight;
    }

    /// <summary>The panel's info reply read at the start.</summary>
    public BeadaPanelInfo Info { get; }

    /// <summary>The stream's size in pixels (<see cref="BeadaPanelInfo.Width"/>, <see cref="BeadaPanelInfo.Height"/>).</summary>
    public int Width => Info.Width;

    public int Height => Info.Height;

    /// <summary>A frame's length in bytes: RGB565, two bytes a pixel.</summary>
    public int FrameLength => Width * Height * 2;

    /// <summary>
    /// The info query a new panel is identified by: opened and claimed safely (<see cref="UsbClaim"/>), GetPanelInfo
    /// written and its 100 bytes read with 1000 ms timeouts, then closed. The USB descriptor's serial is read beside it.
    /// </summary>
    public static BeadaPanelIdentity Identify(IUsbBackend backend, UsbDeviceId id)
    {
        var status = UsbClaim.Open(backend, id, BeadaPanelProtocol.Configuration, BeadaPanelProtocol.Interface, out var device);
        if (status != UsbStatus.Ok)
            return new BeadaPanelIdentity(status, null, BeadaPanelInfoError.None, 0, null);
        try
        {
            status = QueryInfo(device!, BeadaPanelProtocol.IdentifyTimeoutMs, out var info, out var error, out var code);
            var serial = status == UsbStatus.Ok ? device!.ReadSerialNumber() : null;
            return new BeadaPanelIdentity(status, info, error, code, serial);
        }
        finally
        {
            LetGo(device!);
        }
    }

    private static void LetGo(IUsbDevice device)
    {
        device.ReleaseInterface(BeadaPanelProtocol.Interface);
        device.Dispose();
    }

    /// <summary>
    /// The start sequence: wait 300 ms; open and claim; GetPanelInfo and its reply; PanelLinkReset; wait 1000 ms;
    /// SetBacklight at <paramref name="brightness"/>; the stream's start with its format string. Every transfer 2000 ms.
    /// A cancelled start throws <see cref="OperationCanceledException"/> with the panel let go.
    /// </summary>
    public static BeadaPanelStart Start(IUsbBackend backend, UsbDeviceId id, int brightness, TimeProvider time, CancellationToken cancellation)
    {
        var backlightFor = brightness; // checked before anything is opened
        ArgumentOutOfRangeException.ThrowIfLessThan(backlightFor, 0, nameof(brightness));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(backlightFor, 100, nameof(brightness));

        Wait(BeadaPanelProtocol.StartDelay, time, cancellation);
        var status = UsbClaim.Open(backend, id, BeadaPanelProtocol.Configuration, BeadaPanelProtocol.Interface, out var device);
        if (status != UsbStatus.Ok)
            return new BeadaPanelStart(null, status, $"the panel couldn't be opened ({status})");

        var started = false;
        try
        {
            const int timeout = BeadaPanelProtocol.SessionTimeoutMs;
            status = QueryInfo(device!, timeout, out var info, out var error, out var code);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, error == BeadaPanelInfoError.None
                    ? $"the panel didn't answer its info query ({status})"
                    : $"the panel's info reply was refused ({error}, model code {code})");

            status = device!.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.PanelLinkReset), timeout);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, $"the reset wasn't taken ({status})");

            Wait(BeadaPanelProtocol.ResetDelay, time, cancellation);

            var backlight = BeadaPanelProtocol.BrightnessByte(brightness, info!.PanelLinkVersion);
            status = device.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [backlight]), timeout);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, $"the backlight wasn't taken ({status})");

            status = device.BulkWrite(BeadaPanelProtocol.PanelOut, BeadaPanelProtocol.StartStream(info), timeout);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, $"the stream's start wasn't taken ({status})");

            started = true;
            return new BeadaPanelStart(new BeadaPanelSession(device, info, backlight), UsbStatus.Ok, "");
        }
        finally
        {
            if (!started)
                LetGo(device!);
        }
    }

    private static void Wait(TimeSpan delay, TimeProvider time, CancellationToken cancellation) =>
        Task.Delay(delay, time, cancellation).GetAwaiter().GetResult();

    /// <summary>GetPanelInfo on 0x02, then 100 bytes read on 0x82 and parsed.</summary>
    private static UsbStatus QueryInfo(IUsbDevice device, int timeoutMs, out BeadaPanelInfo? info, out BeadaPanelInfoError error, out byte modelCode)
    {
        info = null;
        error = BeadaPanelInfoError.None;
        modelCode = 0;
        var status = device.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.GetPanelInfo), timeoutMs);
        if (status != UsbStatus.Ok)
            return status;
        var reply = new byte[BeadaPanelProtocol.InfoLength];
        status = device.BulkRead(BeadaPanelProtocol.StatusIn, reply, timeoutMs, out var read);
        if (status != UsbStatus.Ok)
            return status;
        if (read == 0)
            return UsbStatus.Io;
        // As 1.4: the whole 100-byte buffer is parsed, whatever the read's length.
        info = BeadaPanelInfo.Parse(reply, out error, out modelCode);
        return info is null ? UsbStatus.NotSupported : UsbStatus.Ok;
    }

    /// <summary>Writes one frame (<see cref="FrameLength"/> bytes of RGB565 little-endian) on the stream.</summary>
    public UsbStatus WriteFrame(ReadOnlySpan<byte> rgb565)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgb565.Length != FrameLength)
            throw new ArgumentException($"A frame is {FrameLength} bytes ({Width} x {Height}, RGB565); this one is {rgb565.Length}.", nameof(rgb565));
        return _device.BulkWrite(BeadaPanelProtocol.PanelOut, rgb565, BeadaPanelProtocol.SessionTimeoutMs);
    }

    /// <summary>Sends the backlight when <paramref name="brightness"/> differs from the last one sent.</summary>
    public UsbStatus SetBrightness(int brightness)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var backlight = BeadaPanelProtocol.BrightnessByte(brightness, Info.PanelLinkVersion);
        if (backlight == _backlight)
            return UsbStatus.Ok;
        var status = _device.BulkWrite(BeadaPanelProtocol.StatusOut,
            BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [backlight]), BeadaPanelProtocol.SessionTimeoutMs);
        if (status == UsbStatus.Ok)
            _backlight = backlight;
        return status;
    }

    /// <summary>
    /// Screen off, once: the backlight at raw 0, a black frame, the stream's end, each 2000 ms; the rest is skipped after
    /// the first failed write. Then the panel is released.
    /// </summary>
    public void Stop()
    {
        if (_stopped || _disposed)
            return;
        _stopped = true;
        const int timeout = BeadaPanelProtocol.SessionTimeoutMs;
        // Raw 0 on every PanelLink version (1.4 sends it unmapped).
        var status = _device.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]), timeout);
        if (status == UsbStatus.Ok)
            status = _device.BulkWrite(BeadaPanelProtocol.PanelOut, new byte[FrameLength], timeout);
        if (status == UsbStatus.Ok)
            status = _device.BulkWrite(BeadaPanelProtocol.PanelOut, BeadaPanelProtocol.EndStream(Info), timeout);
        if (status != UsbStatus.Ok)
            Logger.Information("BeadaPanel {Model}: screen off stopped at a failed write ({Status})", Info.Model.Id, status);
        Dispose();
    }

    /// <summary>Lets the panel go (the interface released, the device closed) without the stop sequence.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        LetGo(_device);
    }
}
