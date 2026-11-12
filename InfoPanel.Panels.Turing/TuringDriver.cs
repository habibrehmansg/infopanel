using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// Turing Smart Screen / TURZX panels (family <c>turing</c>), ported from InfoPanel 1.4: the serial ones, revision A (the
/// 3.5", on a CH340 at <c>1a86:5722</c>), C (the 5") and E (the 8.8" Rev 1.0 and the 2.1"), over their USB serial port
/// at 115200 baud; and the USB ones (<c>1cbe:00xx</c>), over interface 0 with the encrypted protocol Lian Li's panels
/// share, in its Turing variant. <c>panel.json</c> is the one table: the USB id names the model, and a serial model's
/// <c>revision</c> its protocol. The XuanFang 3.5", which shares the 3.5"'s id, is listed but not driven, as in 1.4; a
/// panel saved as one may be tried as the Turing 3.5" (its <c>model</c> setting), and as nothing else.
/// <para>
/// The probe: a USB panel is taken by its id, nothing asked; a revision C or E port is asked Hello, as the library's open
/// did, and taken when it answers as one; a 3.5"'s port is taken at once (revision A has no query). Its start wakes it
/// first, as 1.4 did sleeping panels: its port opened and closed five times a second apart, every time (1.4's loop
/// never ended early), so a start remembers nothing of an earlier one.
/// </para>
/// </summary>
public sealed class TuringDriver : IPanelDriver
{
    /// <summary>The family's id, stored with every panel of it.</summary>
    public const string FamilyId = "turing";

    /// <summary>How many times 1.4 opened and closed a 3.5"'s port to wake it (<c>TuringPanelHelper.cs:66-78</c>).</summary>
    internal const int WakeRounds = 5;

    /// <summary>The serial ports' rate (the library's <c>BaudRate</c>, and 1.4's wake's).</summary>
    internal const int Baud = 115200;

    private const string TriedAs = "turing35";

    private const int WakeOpenTimeoutMs = 1000;

    private static readonly TimeSpan WakeApart = TimeSpan.FromSeconds(1);

    private readonly IPanelDevices? _devices;

    /// <summary>A driver without the device seam: its probe answers <see cref="PanelDeviceStatus.NotSupported"/> and it opens nothing.</summary>
    public TuringDriver()
    {
    }

    /// <summary>A driver that reaches its devices through <paramref name="devices"/>.</summary>
    public TuringDriver(IPanelDevices devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>A pause (1.4's between commands, and the wakes' second); tests: none. Ends early, throwing, when its token is cancelled.</summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; init; } = TuringCatalog.Pause;

    /// <summary>The USB command's timestamp: the library's, the local time of day in milliseconds; tests: a fixed one.</summary>
    internal Func<int> Timestamp { get; init; } = () => (int)DateTime.Now.TimeOfDay.TotalMilliseconds;

    /// <summary>A clock in milliseconds, for the time a serial session's frame write sends for; tests: their own.</summary>
    internal Func<long> Milliseconds { get; init; } = () => Environment.TickCount64;

    public string Family => FamilyId;

    public IReadOnlyList<PanelMatch> Matches => TuringCatalog.Turing.Matches;

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var models = TuringCatalog.ModelsOf(TuringCatalog.Turing, device);
        if (models.Count == 0)
            return new(false, null, PanelDeviceStatus.Ok, "not a Turing panel's USB id");
        if (_devices == null)
            return new(false, null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        var model = models[0];
        if (!TuringCatalog.IsSerial(model))
            return TuringCatalog.ProbeUsb(_devices, device, model);
        var revision = SerialRevision.Of(TuringCatalog.Text(model, "revision"));
        if (revision == null)
            return new(false, null, PanelDeviceStatus.Ok, $"the {model.Name} isn't driven");
        if (revision.Letter == 'A') // revision A has no query: nothing tells a 3.5" from another CH340 on its id, as in 1.4
            return new(true, new PanelIdentity(model.Id, model.Width, model.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok);
        return AskHello(device, model, revision, stop);
    }

    /// <summary>
    /// 1.4's wake of a 3.5"'s port (<c>TuringPanelHelper.cs:61-80,125-165</c>): opened at 115200 and closed, five times
    /// a second apart; a port that wouldn't open counts too, as in 1.4. Each open is given a second, so the wake (about
    /// 4 s, 9 s at most) and the start after it keep within a hosted start's deadline (30 s). Ok, or
    /// <see cref="PanelDeviceStatus.Gone"/> when the panel went away.
    /// </summary>
    private PanelDeviceStatus Wake(PanelCandidate device, CancellationToken stop)
    {
        for (var round = 0; round < WakeRounds; round++)
        {
            if (round > 0)
                Wait(WakeApart, stop);
            var status = _devices!.OpenSerial(device, new PanelSerialOptions(Baud), WakeOpenTimeoutMs, out var serial, stop);
            serial?.Dispose();
            if (status == PanelDeviceStatus.Gone)
                return status;
        }
        return PanelDeviceStatus.Ok;
    }

    /// <summary>A revision C or E port asked Hello, as the library's open did, and let go.</summary>
    private PanelProbe AskHello(PanelCandidate device, PanelManifestModel model, SerialRevision revision, CancellationToken stop)
    {
        var status = _devices!.OpenSerial(device, new PanelSerialOptions(Baud, Dtr: true, Rts: true), TuringCatalog.OpenTimeoutMs, out var serial, stop);
        if (status != PanelDeviceStatus.Ok || serial == null)
            return new(false, null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "its port couldn't be opened");
        string why;
        try
        {
            status = TuringSerialScreen.Create(revision, serial).Open(stop, out why);
        }
        finally
        {
            serial.Dispose();
        }
        if (status == PanelDeviceStatus.Ok)
            return new(true, new PanelIdentity(model.Id, model.Width, model.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok);
        return status == PanelDeviceStatus.NotSupported ? new(false, null, PanelDeviceStatus.Ok, why) : new(false, null, status, why);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var models = TuringCatalog.ModelsOf(TuringCatalog.Turing, device);
        if (models.Count == 0)
            return new(null, PanelDeviceStatus.NotSupported, "not a Turing panel's USB id");
        if (_devices == null)
            return new(null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        var model = models.FirstOrDefault(m => m.Id == identity.Model) ?? models[0];

        // "Try it as…": only the 3.5"'s port has a model to try, revision A.
        if (TuringCatalog.TriedModel(options.FamilySettings) is { } tried && tried != model.Id)
        {
            if (tried != TriedAs || models.All(m => m.Id != TriedAs))
                return new(null, PanelDeviceStatus.NotSupported, "only the Turing Smart Screen 3.5\" (revision A) can be tried, and only on its port");
            model = models.First(m => m.Id == TriedAs);
        }

        if (!TuringCatalog.IsSerial(model))
        {
            var status = _devices.OpenUsb(device, 0, TuringCatalog.OpenTimeoutMs, out var usb, stop);
            if (status != PanelDeviceStatus.Ok || usb == null)
                return new(null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "the panel couldn't be opened");
            var session = new TuringUsbSession(UsbVariant.Turing, usb, model.Width, model.Height, Timestamp, () => DateTime.Now, Wait);
            var why = "";
            return TuringCatalog.Started(session, () => session.Start(options.Brightness, stop, out why), () => why);
        }

        var revision = SerialRevision.Of(TuringCatalog.Text(model, "revision"));
        if (revision == null)
            return new(null, PanelDeviceStatus.NotSupported,
                $"the {model.Name} isn't driven (1.4 didn't drive it either); its model setting can try it as the Turing Smart Screen 3.5\"");
        if (revision.Letter == 'A' && Wake(device, stop) == PanelDeviceStatus.Gone)
            return new(null, PanelDeviceStatus.Gone, "the panel went away while it was being woken");
        var opened = _devices.OpenSerial(device, new PanelSerialOptions(Baud, Dtr: true, Rts: true), TuringCatalog.OpenTimeoutMs, out var serial, stop);
        if (opened != PanelDeviceStatus.Ok || serial == null)
            return new(null, opened == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : opened, "the panel's port couldn't be opened");
        var serialSession = new TuringSerialSession(revision, serial, model.Width, model.Height, Milliseconds);
        var serialWhy = "";
        return TuringCatalog.Started(serialSession, () => serialSession.Start(options.Brightness, stop, out serialWhy), () => serialWhy);
    }
}
