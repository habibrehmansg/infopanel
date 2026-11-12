using System.Diagnostics;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Thermalright;

/// <summary>
/// One start of a Thermalright panel, as InfoPanel 1.4's device task ran it (<c>Services/ThermalrightPanelDeviceTask.cs</c>
/// at the 1.4.x tip): the protocol's init and its reply, the model and size from the panel's saved model and the reply,
/// then a session. The <c>model</c> setting, when it names a model, is the one driven (at its size). A start whose
/// model is still unknown, or is another protocol's (a reply's identifier can name one), answers
/// <see cref="PanelDeviceStatus.NotSupported"/> until that setting names one.
/// </summary>
internal sealed class ThermalrightStart(ThermalrightDriver driver, IPanelDevices devices, PanelCandidate device, ThermalrightDriver.Way way,
    ThermalrightModel? start, ThermalrightModel? chosen, ThermalrightSettings settings)
{
    private ThermalrightCatalog Catalog => driver.Catalog;

    private static PanelOpen Failed(PanelDeviceStatus status, string why) => new(null, status, why);

    /// <summary>ChiZhu (:699-877): the init and its reply, asked again 3 s later while the panel boots, 5 tries in all.</summary>
    public PanelOpen ChiZhu(CancellationToken stop)
    {
        var status = driver.OpenBulk(device, ThermalrightDriver.InitTimeoutMs, stop, out var usb, out var outEndpoint, out var inEndpoint);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "it couldn't be opened");
        try
        {
            var reply = new byte[ChiZhuProtocol.ReplySize];
            var read = 0;
            for (var attempt = 0; attempt < ChiZhuProtocol.BootTries; attempt++)
            {
                if (attempt > 0)
                    driver.Wait(ChiZhuProtocol.BootRetryDelay, stop);
                status = usb!.BulkWrite(outEndpoint, ChiZhuProtocol.InitCommand(), ThermalrightDriver.InitTimeoutMs, cancellationToken: stop);
                if (status != PanelDeviceStatus.Ok)
                    return Release(usb, status, "its init wasn't taken");
                status = usb.BulkRead(inEndpoint, reply, ThermalrightDriver.InitTimeoutMs, out read, stop);
                if (status == PanelDeviceStatus.Gone)
                    return Release(usb, status, "it went away");
                if (status == PanelDeviceStatus.Ok && ChiZhuProtocol.IsBooting(reply.AsSpan(0, read)))
                    continue;
                break;
            }
            // A reply that never came, or a panel still booting after the last try, is read as 1.4 read it.
            var answer = status == PanelDeviceStatus.Ok ? reply.AsSpan(0, read) : [];
            var detected = ThermalrightDetect.ChiZhuSession(Catalog, start, answer);
            driver.Wait(TimeSpan.FromMilliseconds(100), stop);
            return Session(usb!, detected, (model, width, height, capabilities) =>
                new ChiZhuSession(capabilities, usb!, outEndpoint, model.Pixels, driver.Wait));
        }
        catch
        {
            usb!.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Trofeo over HID (:1589-1853): each of the device's data collections in turn, until one answers its init (one that
    /// can't be opened or doesn't answer is let go first); the init, 3 tries 500 ms apart (50 ms before each, 200 ms
    /// before its reply). The model is the saved one or the reply's: the first model with the device's ids (1.4's scan)
    /// isn't taken for a panel whose reply names none.
    /// </summary>
    public PanelOpen Hid(CancellationToken stop)
    {
        var status = devices.ListHid(device, ThermalrightDriver.HidFilter, out var collections);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "its HID collections couldn't be listed");
        var failed = Failed(PanelDeviceStatus.NotSupported, "it has no HID collection with 513-byte output reports");
        foreach (var collection in collections)
        {
            status = devices.OpenHid(device, collection, ThermalrightDriver.InitTimeoutMs, out var hid, stop);
            if (status == PanelDeviceStatus.Gone)
                return Failed(status, "it went away");
            if (status != PanelDeviceStatus.Ok)
            {
                failed = Failed(status, "its HID collection couldn't be opened");
                continue;
            }
            try
            {
                status = HidInit(hid!, stop, out var response);
                if (status == PanelDeviceStatus.Gone)
                    return Release(hid!, status, "it went away");
                if (response == null)
                {
                    failed = Release(hid!, PanelDeviceStatus.Io, "its HID init wasn't answered in 3 tries");
                    continue;
                }
                var detected = ThermalrightDetect.HidSession(Catalog, start, response);
                driver.Wait(TimeSpan.FromMilliseconds(100), stop);
                return Session(hid!, detected, (model, width, height, capabilities) =>
                    new TrofeoHidSession(capabilities, hid!, width, height, model.Pixels, driver.Wait));
            }
            catch
            {
                hid!.Dispose();
                throw;
            }
        }
        return failed;
    }

    // The HID init's tries; the reply without its report id, or null when none came (a report of its id alone is none).
    private PanelDeviceStatus HidInit(IPanelHid hid, CancellationToken stop, out byte[]? response)
    {
        response = null;
        var report = new byte[Math.Max(hid.MaxInputReportLength, TrofeoProtocol.PacketSize + 1)];
        for (var attempt = 1; attempt <= 3 && response == null; attempt++)
        {
            if (attempt > 1)
                driver.Wait(TimeSpan.FromMilliseconds(500), stop);
            driver.Wait(TimeSpan.FromMilliseconds(50), stop);
            var status = hid.Write(TrofeoProtocol.HidReport(TrofeoProtocol.InitPacket()), ThermalrightDriver.InitTimeoutMs, stop);
            if (status == PanelDeviceStatus.Gone)
                return status;
            if (status != PanelDeviceStatus.Ok)
                continue;
            driver.Wait(TimeSpan.FromMilliseconds(200), stop);
            status = hid.Read(report, ThermalrightDriver.InitTimeoutMs, out var read, stop);
            if (status == PanelDeviceStatus.Gone)
                return status;
            if (status == PanelDeviceStatus.Ok && TrofeoProtocol.FromHidReport(report.AsSpan(0, read)) is { Length: > 0 } answered)
                response = answered;
        }
        return PanelDeviceStatus.Ok;
    }

    /// <summary>
    /// TrofeoBulk LY (:963-1206; 50 ms, then the 2048-byte init) and LY1 (:1274-1393; the 512-byte init), each reply read
    /// after its write, then 1.4's 200 ms before the reply is checked (:1002, :1311; 1.4 waited them while the write and
    /// the read ran, so the panel settles at least as long), then 100 ms.
    /// </summary>
    public PanelOpen TrofeoBulk(bool ly1, CancellationToken stop)
    {
        var status = driver.OpenBulk(device, ThermalrightDriver.InitTimeoutMs, stop, out var usb, out var outEndpoint, out var inEndpoint);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "it couldn't be opened");
        try
        {
            if (!ly1)
                driver.Wait(TimeSpan.FromMilliseconds(50), stop);
            var protocol = ly1 ? ThermalrightProtocol.TrofeoBulkLy1 : ThermalrightProtocol.TrofeoBulk;
            status = usb!.BulkWrite(outEndpoint, TrofeoBulkProtocol.InitPacket(protocol), ThermalrightDriver.InitTimeoutMs, cancellationToken: stop);
            if (status != PanelDeviceStatus.Ok)
                return Release(usb, status, "its init wasn't taken");
            var reply = new byte[ly1 ? TrofeoBulkProtocol.Ly1ReplySize : TrofeoBulkProtocol.ReplySize];
            status = usb.BulkRead(inEndpoint, reply, ThermalrightDriver.ReplyTimeoutMs, out var read, stop);
            if (status == PanelDeviceStatus.Gone)
                return Release(usb, status, "it went away");
            driver.Wait(TimeSpan.FromMilliseconds(200), stop);
            var answer = status == PanelDeviceStatus.Ok ? reply.AsSpan(0, read) : []; // no reply: 1.4 went on
            var from = start ?? Catalog.ByVidPid(device.VendorId, device.ProductId);
            var detected = ly1 ? ThermalrightDetect.TrofeoBulkLy1Session(Catalog, from, answer) : ThermalrightDetect.TrofeoBulkSession(Catalog, from, answer);
            driver.Wait(TimeSpan.FromMilliseconds(100), stop);
            return Session(usb, detected, (model, width, height, capabilities) =>
                new TrofeoBulkSession(capabilities, usb, outEndpoint, inEndpoint, ly1));
        }
        catch
        {
            usb!.Dispose();
            throw;
        }
    }

    /// <summary>ALi (:1468-1587): the 1040-byte init, then its reply (1.4 read while it wrote; here after).</summary>
    public PanelOpen Ali(CancellationToken stop)
    {
        var status = driver.OpenBulk(device, ThermalrightDriver.InitTimeoutMs, stop, out var usb, out var outEndpoint, out var inEndpoint);
        if (status != PanelDeviceStatus.Ok)
            return Failed(status, "it couldn't be opened");
        try
        {
            status = usb!.BulkWrite(outEndpoint, AliProtocol.InitPacket(), ThermalrightDriver.InitTimeoutMs, cancellationToken: stop);
            if (status != PanelDeviceStatus.Ok)
                return Release(usb, status, "its init wasn't taken");
            var reply = new byte[1024];
            status = usb.BulkRead(inEndpoint, reply, ThermalrightDriver.ReplyTimeoutMs, out var read, stop);
            if (status == PanelDeviceStatus.Gone)
                return Release(usb, status, "it went away");
            var answer = status == PanelDeviceStatus.Ok ? reply.AsSpan(0, read) : [];
            var from = start ?? Catalog.Models.First(m => m.Protocol == ThermalrightProtocol.Ali && m.AliFallback); // 1.4's 320 × 320 default
            var detected = ThermalrightDetect.AliSession(Catalog, from, answer);
            var replied = answer.Length;
            driver.Wait(TimeSpan.FromMilliseconds(100), stop);
            return Session(usb, detected, (model, width, height, capabilities) =>
                new AliSession(capabilities, usb, outEndpoint, inEndpoint, ThermalrightDetect.AliFrameBytes(replied, width, height)));
        }
        catch
        {
            usb!.Dispose();
            throw;
        }
    }

    private static PanelOpen Release(IDisposable handle, PanelDeviceStatus status, string why)
    {
        handle.Dispose();
        return Failed(status, why);
    }

    // The model driven (the setting's, else the start's), its size and crop, and the session; an unknown model, or one of
    // another protocol (as the setting's is refused), lets the device go.
    private PanelOpen Session(IDisposable handle, ThermalrightDetection detected,
        Func<ThermalrightModel, int, int, PanelCapabilities, IPanelSession> make)
    {
        var model = chosen ?? detected.Model;
        if (model == null)
            return Release(handle, PanelDeviceStatus.NotSupported, "its reply names no model the family knows: choose its model in the model setting");
        if (chosen == null && ThermalrightDriver.WayOf(model) != way)
            return Release(handle, PanelDeviceStatus.NotSupported,
                $"its reply names {model.Id}, a model this panel's protocol doesn't drive: choose its model in the model setting");
        var (width, height) = chosen != null ? (chosen.Width, chosen.Height) : (detected.Width, detected.Height);
        var cropRows = chosen != null ? (chosen.Height == 480 ? chosen.FlickerFixRows : null) : detected.CropRows;
        var capabilities = ThermalrightDriver.CapabilitiesOf(model, width, height, cropRows, settings);
        return new PanelOpen(make(model, width, height, capabilities), PanelDeviceStatus.Ok, "");
    }
}

/// <summary>
/// A running Thermalright panel: one format, frames over the JPEG cap left unsent (TRCC drops them; the engine encodes
/// again lower first), the brightness the engine's (it dims the frames), and a stop that sends nothing, as 1.4's stop
/// sent nothing: the panel keeps its last frame, and the device is let go.
/// </summary>
internal abstract class ThermalrightSession(PanelCapabilities capabilities, IDisposable handle) : IPanelSession
{
    private bool _closed;

    public PanelCapabilities Capabilities => capabilities;

    /// <summary>Frames left unsent because they were over the cap.</summary>
    public int Dropped { get; private set; }

    public PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop)
    {
        if (_closed)
            return new PanelWrite(PanelDeviceStatus.Io, TimeSpan.Zero);
        if (frame.Format != capabilities.Formats[0])
            return new PanelWrite(PanelDeviceStatus.NotSupported, TimeSpan.Zero);
        if (capabilities.MaxFrameBytes is { } cap && frame.Bytes.Length > cap)
        {
            Dropped++;
            return new PanelWrite(PanelDeviceStatus.Ok, TimeSpan.Zero);
        }
        var clock = Stopwatch.StartNew();
        var status = Send(frame.Bytes, stop);
        var time = clock.Elapsed;
        if (status == PanelDeviceStatus.Ok)
            After(stop);
        return new PanelWrite(status, time);
    }

    /// <summary>The frame's transfers, timed as the device's.</summary>
    protected abstract PanelDeviceStatus Send(ReadOnlySpan<byte> frame, CancellationToken stop);

    /// <summary>A protocol's gap after a frame (not the device's time).</summary>
    protected virtual void After(CancellationToken stop)
    {
    }

    public PanelDeviceStatus SetBrightness(int percent) => _closed ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok;

    public void Close(bool screenOff, TimeSpan budget)
    {
        if (_closed)
            return;
        _closed = true;
        handle.Dispose();
    }

    public void Dispose() => Close(false, TimeSpan.Zero);
}

/// <summary>ChiZhu's frame (:851-876): one write of the header and the pixels (500 ms), a zero-length packet after one that fills its last 512-byte packet, then 15 ms.</summary>
internal sealed class ChiZhuSession(PanelCapabilities capabilities, IPanelUsb usb, byte outEndpoint, ThermalrightPixels pixels, Action<TimeSpan, CancellationToken> wait)
    : ThermalrightSession(capabilities, usb)
{
    public const int WriteTimeoutMs = 500;

    protected override PanelDeviceStatus Send(ReadOnlySpan<byte> frame, CancellationToken stop)
    {
        var packet = ChiZhuProtocol.FramePacket(frame, Capabilities.Width, Capabilities.Height, pixels);
        return usb.BulkWrite(outEndpoint, packet, WriteTimeoutMs, ChiZhuProtocol.NeedsZeroLengthPacket(packet.Length), stop);
    }

    protected override void After(CancellationToken stop) => wait(ChiZhuProtocol.FrameGap, stop);
}

/// <summary>
/// Trofeo's frame over HID (<c>HidPanelDevice</c>): 512-byte packets as 513-byte reports (5 s each), an RGB565 frame
/// resting 1 ms after every 32nd packet; then 20 ms after an RGB565 frame, 1 ms after a JPEG one (:1846-1853).
/// </summary>
internal sealed class TrofeoHidSession(PanelCapabilities capabilities, IPanelHid hid, int width, int height, ThermalrightPixels pixels, Action<TimeSpan, CancellationToken> wait)
    : ThermalrightSession(capabilities, hid)
{
    public const int WriteTimeoutMs = 5000;

    private bool Rgb565 => pixels != ThermalrightPixels.Jpeg;

    protected override PanelDeviceStatus Send(ReadOnlySpan<byte> frame, CancellationToken stop)
    {
        var packets = TrofeoProtocol.FramePackets(frame, width, height, pixels);
        var count = 1;
        for (var i = 0; i < packets.Count; i++)
        {
            var status = hid.Write(TrofeoProtocol.HidReport(packets[i]), WriteTimeoutMs, stop);
            if (status != PanelDeviceStatus.Ok)
                return status;
            if (i > 0 && Rgb565 && ++count % 32 == 0)
                wait(TimeSpan.FromMilliseconds(1), stop);
        }
        return PanelDeviceStatus.Ok;
    }

    protected override void After(CancellationToken stop) => wait(TimeSpan.FromMilliseconds(Rgb565 ? 20 : 1), stop);
}

/// <summary>
/// TrofeoBulk's frame: LY in 4096-byte writes (:1136-1205), LY1 in one (:1375-1459), 1 s each; then the panel's
/// acknowledgement (500 ms), five missing in a row ending the session.
/// </summary>
internal sealed class TrofeoBulkSession(PanelCapabilities capabilities, IPanelUsb usb, byte outEndpoint, byte inEndpoint, bool ly1)
    : ThermalrightSession(capabilities, usb)
{
    public const int WriteTimeoutMs = 1000;
    public const int AckTimeoutMs = 500;

    private readonly byte[] _ack = new byte[ly1 ? TrofeoBulkProtocol.Ly1ReplySize : TrofeoBulkProtocol.ReplySize];
    private int _ackFailures;

    protected override PanelDeviceStatus Send(ReadOnlySpan<byte> frame, CancellationToken stop)
    {
        PanelDeviceStatus status;
        if (ly1)
        {
            status = usb.BulkWrite(outEndpoint, TrofeoBulkProtocol.Ly1Frame(frame), WriteTimeoutMs, cancellationToken: stop);
        }
        else
        {
            var packets = TrofeoBulkProtocol.LyFrame(frame);
            status = PanelDeviceStatus.Ok;
            foreach (var (offset, length) in TrofeoBulkProtocol.LyTransfers(packets.Length))
            {
                status = usb.BulkWrite(outEndpoint, packets.AsSpan(offset, length), WriteTimeoutMs, cancellationToken: stop);
                if (status != PanelDeviceStatus.Ok)
                    break;
            }
        }
        if (status != PanelDeviceStatus.Ok)
            return status;

        var ack = usb.BulkRead(inEndpoint, _ack, AckTimeoutMs, out var read, stop);
        if (ack == PanelDeviceStatus.Gone)
            return ack;
        if (ack == PanelDeviceStatus.Ok && read > 0)
        {
            _ackFailures = 0;
            return PanelDeviceStatus.Ok;
        }
        return ++_ackFailures >= TrofeoBulkProtocol.MaxAckFailures ? PanelDeviceStatus.Io : PanelDeviceStatus.Ok;
    }
}

/// <summary>ALi's frame (:1562-1586): the header and the frame size's pixels in one write (100 ms), then its 16-byte acknowledgement (100 ms); either failing ends the session.</summary>
internal sealed class AliSession(PanelCapabilities capabilities, IPanelUsb usb, byte outEndpoint, byte inEndpoint, int frameSize)
    : ThermalrightSession(capabilities, usb)
{
    public const int TimeoutMs = 100;

    private readonly byte[] _ack = new byte[AliProtocol.AckSize];

    protected override PanelDeviceStatus Send(ReadOnlySpan<byte> frame, CancellationToken stop)
    {
        var status = usb.BulkWrite(outEndpoint, AliProtocol.FramePacket(frame, frameSize), TimeoutMs, cancellationToken: stop);
        return status != PanelDeviceStatus.Ok ? status : usb.BulkRead(inEndpoint, _ack, TimeoutMs, out _, stop);
    }
}
