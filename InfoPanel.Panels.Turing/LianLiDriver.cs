using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// Lian Li's LCD panels (family <c>lianli</c>): the Universal Screen 8.8" and 9.2" and the HydroShift II OLED Curve and
/// LCD (<c>1cbe:a0xx</c>), over interface 0 with the encrypted USB protocol Turing's USB panels share, in its Lian Li
/// variant, ported from InfoPanel 1.4's <c>LianLiUsbScreenDevice</c> and <c>LianLiPanelDeviceTask</c>. The USB id names
/// the model (<c>panel.json</c>), so the probe asks the device nothing. A session's start syncs, stops the panel's own
/// media, clears its two layers (a transparent PNG and a black JPEG), sets its clock and frame rate and its brightness;
/// the stop stops its media and turns the brightness to 0, as 1.4 did.
/// </summary>
public sealed class LianLiDriver : IPanelDriver
{
    /// <summary>The family's id, stored with every panel of it (1.x's <c>lianLi</c>).</summary>
    public const string FamilyId = "lianli";

    /// <summary>1.4's timestamps counted from the day before it started (<c>LianLiUsbScreenDevice.cs:27</c>).</summary>
    private static readonly DateTime AppStartTime = DateTime.Today.AddDays(-1.0);

    private readonly IPanelDevices? _devices;

    /// <summary>A driver without the device seam: its probe answers <see cref="PanelDeviceStatus.NotSupported"/> and it opens nothing.</summary>
    public LianLiDriver()
    {
    }

    /// <summary>A driver that reaches its devices through <paramref name="devices"/>.</summary>
    public LianLiDriver(IPanelDevices devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>A pause (1.4's between commands); tests: none. Ends early, throwing, when its token is cancelled.</summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; init; } = TuringCatalog.Pause;

    /// <summary>The command's timestamp, as 1.4 counted it (<c>LianLiUsbScreenDevice.cs:211</c>); tests: a fixed one.</summary>
    internal Func<int> Timestamp { get; init; } = () => unchecked((int)(DateTime.UtcNow - AppStartTime).TotalMilliseconds);

    /// <summary>The clock the panel is set to (<c>LianLiUsbScreenDevice.cs:85</c>); tests: a fixed one.</summary>
    internal Func<DateTime> LocalNow { get; init; } = () => DateTime.Now;

    public string Family => FamilyId;

    public IReadOnlyList<PanelMatch> Matches => TuringCatalog.LianLi.Matches;

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var models = TuringCatalog.ModelsOf(TuringCatalog.LianLi, device);
        if (models.Count == 0)
            return new(false, null, PanelDeviceStatus.Ok, "not a Lian Li panel's USB id");
        if (_devices == null)
            return new(false, null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        return TuringCatalog.ProbeUsb(_devices, device, models[0]);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var models = TuringCatalog.ModelsOf(TuringCatalog.LianLi, device);
        if (models.Count == 0)
            return new(null, PanelDeviceStatus.NotSupported, "not a Lian Li panel's USB id");
        if (_devices == null)
            return new(null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        var model = models[0];
        var status = _devices.OpenUsb(device, 0, TuringCatalog.OpenTimeoutMs, out var usb, stop);
        if (status != PanelDeviceStatus.Ok || usb == null)
            return new(null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "the panel couldn't be opened");
        var session = new TuringUsbSession(UsbVariant.LianLi, usb, model.Width, model.Height, Timestamp, LocalNow, Wait);
        var why = "";
        return TuringCatalog.Started(session, () => session.Start(options.Brightness, stop, out why), () => why);
    }
}
