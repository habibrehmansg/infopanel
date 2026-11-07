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

    /// <summary>The most the screen-off may take, all its writes together, counted from the stop's ask.</summary>
    public static readonly TimeSpan ScreenOffBound = TimeSpan.FromSeconds(3);

    private readonly IUsbDevice _device;
    private readonly TimeProvider _time;
    private byte _backlight;
    private bool _stopped;
    private bool _disposed;

    private BeadaPanelSession(IUsbDevice device, BeadaPanelInfo info, byte backlight, TimeProvider time)
    {
        _device = device;
        _time = time;
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
            status = QueryInfo(device!, BeadaPanelProtocol.IdentifyTimeoutMs, CancellationToken.None, out var info, out var error, out var code);
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
    /// A cancelled start throws <see cref="OperationCanceledException"/> with the panel let go: cancellation is checked
    /// between transfers, and a start that got as far as the reset turns the screen off first, within
    /// <see cref="ScreenOffBound"/> counted from the cancellation.
    /// </summary>
    public static BeadaPanelStart Start(IUsbBackend backend, UsbDeviceId id, int brightness, TimeProvider time, CancellationToken cancellation)
    {
        var backlightFor = brightness; // checked before anything is opened
        ArgumentOutOfRangeException.ThrowIfLessThan(backlightFor, 0, nameof(brightness));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(backlightFor, 100, nameof(brightness));

        long cancelledAt = 0;
        using var cancelled = cancellation.Register(() => Interlocked.Exchange(ref cancelledAt, time.GetTimestamp()));

        Wait(BeadaPanelProtocol.StartDelay, time, cancellation);
        var status = UsbClaim.Open(backend, id, BeadaPanelProtocol.Configuration, BeadaPanelProtocol.Interface, out var device);
        if (status != UsbStatus.Ok)
            return new BeadaPanelStart(null, status, $"the panel couldn't be opened ({status})");

        var started = false;
        BeadaPanelInfo? changed = null; // set once a write that changes the panel's state was tried
        try
        {
            const int timeout = BeadaPanelProtocol.SessionTimeoutMs;
            cancellation.ThrowIfCancellationRequested();
            status = QueryInfo(device!, timeout, cancellation, out var info, out var error, out var code);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, error == BeadaPanelInfoError.None
                    ? $"the panel didn't answer its info query ({status})"
                    : $"the panel's info reply was refused ({error}, model code {code})");

            cancellation.ThrowIfCancellationRequested();
            changed = info;
            status = device!.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.PanelLinkReset), timeout);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, $"the reset wasn't taken ({status})");

            Wait(BeadaPanelProtocol.ResetDelay, time, cancellation);

            var backlight = BeadaPanelProtocol.BrightnessByte(brightness, info!.PanelLinkVersion);
            status = device.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [backlight]), timeout);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, $"the backlight wasn't taken ({status})");

            cancellation.ThrowIfCancellationRequested();
            status = device.BulkWrite(BeadaPanelProtocol.PanelOut, BeadaPanelProtocol.StartStream(info), timeout);
            if (status != UsbStatus.Ok)
                return new BeadaPanelStart(null, status, $"the stream's start wasn't taken ({status})");

            started = true;
            return new BeadaPanelStart(new BeadaPanelSession(device, info, backlight, time), UsbStatus.Ok, "");
        }
        finally
        {
            if (!started)
            {
                // A start that got as far as changing the panel turns its screen off again before letting it go.
                if (changed != null)
                    ScreenOff(device!, changed, time, cancellation.IsCancellationRequested ? Interlocked.Read(ref cancelledAt) : time.GetTimestamp());
                LetGo(device!);
            }
        }
    }

    private static void Wait(TimeSpan delay, TimeProvider time, CancellationToken cancellation) =>
        Task.Delay(delay, time, cancellation).GetAwaiter().GetResult();

    /// <summary>
    /// GetPanelInfo on 0x02, then 100 bytes read on 0x82 and parsed. A cancellation during the write throws
    /// <see cref="OperationCanceledException"/> before the read is started.
    /// </summary>
    private static UsbStatus QueryInfo(IUsbDevice device, int timeoutMs, CancellationToken cancellation,
        out BeadaPanelInfo? info, out BeadaPanelInfoError error, out byte modelCode)
    {
        info = null;
        error = BeadaPanelInfoError.None;
        modelCode = 0;
        var status = device.BulkWrite(BeadaPanelProtocol.StatusOut, BeadaPanelProtocol.StatusLink(StatusLinkType.GetPanelInfo), timeoutMs);
        if (status != UsbStatus.Ok)
            return status;
        cancellation.ThrowIfCancellationRequested();
        var reply = new byte[BeadaPanelProtocol.InfoLength];
        status = device.BulkRead(BeadaPanelProtocol.StatusIn, reply, timeoutMs, out var read);
        if (status != UsbStatus.Ok)
            return status;
        if (read == 0)
            return UsbStatus.Io;
        // Only the bytes received: a short reply is refused (1.4 parsed its whole zeroed buffer, so a short reply passed
        // for a model-5 panel of no size).
        info = BeadaPanelInfo.Parse(reply.AsSpan(0, read), out error, out modelCode);
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
    /// Screen off, once: the backlight at raw 0, a black frame, the stream's end (<see cref="ScreenOff"/>). Then the panel
    /// is released. <paramref name="since"/>: when the stop was asked for (a timestamp of the session's
    /// <see cref="TimeProvider"/>), which <see cref="ScreenOffBound"/> counts from; null: now.
    /// </summary>
    public void Stop(long? since = null)
    {
        if (_stopped || _disposed)
            return;
        _stopped = true;
        try
        {
            ScreenOff(_device, Info, _time, since ?? _time.GetTimestamp());
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// The screen-off sequence, as 1.4 sends it: the backlight at raw 0 (any PanelLink version), a black frame, the
    /// stream's end, each tried whatever the one before came to, but none once the panel is gone
    /// (<see cref="UsbStatus.NoDevice"/>), and all within <see cref="ScreenOffBound"/> from <paramref name="started"/>: each
    /// write's timeout is what is left of it (at most 2000 ms), and a write isn't started once it is spent. The first
    /// failure, or Ok.
    /// </summary>
    private static UsbStatus ScreenOff(IUsbDevice device, BeadaPanelInfo info, TimeProvider time, long started)
    {
        var first = UsbStatus.Ok;
        var steps = new (byte Endpoint, Func<byte[]> Data)[]
        {
            (BeadaPanelProtocol.StatusOut, () => BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0])),
            (BeadaPanelProtocol.PanelOut, () => new byte[info.Width * info.Height * 2]),
            (BeadaPanelProtocol.PanelOut, () => BeadaPanelProtocol.EndStream(info)),
        };
        foreach (var (endpoint, data) in steps)
        {
            var left = ScreenOffBound - time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
            {
                Logger.Information("BeadaPanel {Model}: screen off stopped at its {Bound} s bound", info.Model.Id, ScreenOffBound.TotalSeconds);
                return first == UsbStatus.Ok ? UsbStatus.Timeout : first;
            }
            var timeout = (int)Math.Min(BeadaPanelProtocol.SessionTimeoutMs, Math.Ceiling(left.TotalMilliseconds));
            var status = device.BulkWrite(endpoint, data(), timeout);
            if (status == UsbStatus.Ok)
                continue;
            if (first == UsbStatus.Ok)
                first = status;
            if (status == UsbStatus.NoDevice)
            {
                Logger.Information("BeadaPanel {Model}: screen off stopped, the panel is gone", info.Model.Id);
                return first;
            }
            Logger.Information("BeadaPanel {Model}: a screen-off write failed ({Status}); the rest is still sent", info.Model.Id, status);
        }
        return first;
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
