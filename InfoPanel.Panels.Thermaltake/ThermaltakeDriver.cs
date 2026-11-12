using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Thermaltake;

/// <summary>
/// Thermaltake / ASRock LCD panels (family <c>thermaltake</c>): the Thermaltake 6" LCD and the ASRock Phantom Gaming 360
/// LCD, one HID protocol (<see cref="ThermaltakeProtocol"/>), ported from InfoPanel 1.4. The USB id decides the model
/// (<c>panel.json</c>'s <c>usb</c> member of each model), so the probe asks the device nothing: it checks the device has
/// a HID collection whose output report holds a packet, as 1.4's scan did. A session opens the first such collection that
/// opens, sends <c>POST conn 1</c> and <c>POST realtimeDisplay 1</c> on, each answered <c>200</c>, then takes JPEG
/// frames in 1000-byte chunks; the engine dims the frame for the brightness, as 1.4 did; the stop turns realtime
/// display off, as 1.4's dispose of the device did.
/// </summary>
public sealed class ThermaltakeDriver : IPanelDriver
{
    /// <summary>The family's id, stored with every panel of it.</summary>
    public const string FamilyId = "thermaltake";

    /// <summary>How long a collection's open may take.</summary>
    internal const int OpenTimeoutMs = 5000;

    /// <summary>The time a failed or cancelled start gives its screen-off: the engine's bound for a stop.</summary>
    internal static readonly TimeSpan StartCloseBudget = TimeSpan.FromSeconds(3);

    private static readonly PanelHidFilter Collection = new(MinOutputReport: ThermaltakeProtocol.MinOutputReport);

    private static readonly Lazy<PanelManifestFamily> Manifest = new(() => PanelManifest.Read(typeof(ThermaltakeDriver).Assembly).Family(FamilyId));

    private readonly IPanelDevices? _devices;

    /// <summary>A driver without the device seam: its probe answers <see cref="PanelDeviceStatus.NotSupported"/> and it opens nothing.</summary>
    public ThermaltakeDriver()
    {
    }

    /// <summary>A driver that reaches its devices through <paramref name="devices"/>.</summary>
    public ThermaltakeDriver(IPanelDevices devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>The <c>Date</c> header's clock, in Unix milliseconds (tests: a fixed one).</summary>
    internal Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The pause before a reply is read (tests: none); a start's ends early, throwing, when its token is cancelled.</summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; init; } = Pause;

    public string Family => FamilyId;

    public IReadOnlyList<PanelMatch> Matches => Manifest.Value.Matches;

    /// <summary>The model <paramref name="device"/>'s USB id names, or null.</summary>
    internal static PanelManifestModel? ModelOf(PanelCandidate device)
    {
        var usb = $"{device.VendorId:x4}:{device.ProductId:x4}";
        return Manifest.Value.Models.FirstOrDefault(m => m.Json.TryGetProperty("usb", out var id) && id.GetString() == usb);
    }

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        if (ModelOf(device) is not { } model)
            return new(false, null, PanelDeviceStatus.Ok, "not a Thermaltake or ASRock panel's USB id");
        if (_devices == null)
            return new(false, null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        var status = _devices.ListHid(device, Collection, out var collections);
        if (status != PanelDeviceStatus.Ok)
            return new(false, null, status, "its HID collections couldn't be listed");
        if (collections.Count == 0)
            return new(false, null, PanelDeviceStatus.Ok, $"it has no HID collection whose output report holds {ThermaltakeProtocol.MinOutputReport} bytes");
        return new(true, new PanelIdentity(model.Id, model.Width, model.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (ModelOf(device) is not { } model)
            return new(null, PanelDeviceStatus.NotSupported, "not a Thermaltake or ASRock panel's USB id");
        if (_devices == null)
            return new(null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        var status = _devices.ListHid(device, Collection, out var collections);
        if (status != PanelDeviceStatus.Ok)
            return new(null, status, "its HID collections couldn't be listed");
        if (collections.Count == 0)
            return new(null, PanelDeviceStatus.NotSupported, $"it has no HID collection whose output report holds {ThermaltakeProtocol.MinOutputReport} bytes");

        // 1.4 opened each such collection in turn and kept the first that opened.
        IPanelHid? hid = null;
        foreach (var collection in collections)
        {
            status = _devices.OpenHid(device, collection, OpenTimeoutMs, out hid, stop);
            if (status == PanelDeviceStatus.Ok && hid != null)
                break;
            hid?.Dispose();
            hid = null;
            if (status == PanelDeviceStatus.Gone)
                break;
        }
        if (hid == null)
            return new(null, status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status, "the panel couldn't be opened");

        var session = new ThermaltakeSession(hid, model.Width, model.Height, Now, Wait);
        try
        {
            status = session.Handshake(stop, out var why);
            if (status == PanelDeviceStatus.Ok)
                return new(session, status, "");
            // 1.4 disposed the device after a failed handshake, and its dispose turned realtime display off.
            session.Close(screenOff: true, StartCloseBudget);
            session.Dispose();
            return new(null, status, why);
        }
        catch (OperationCanceledException)
        {
            session.Close(screenOff: true, StartCloseBudget);
            session.Dispose();
            throw;
        }
    }

    private static void Pause(TimeSpan time, CancellationToken stop)
    {
        if (stop.CanBeCanceled)
            stop.WaitHandle.WaitOne(time);
        else
            Thread.Sleep(time);
        stop.ThrowIfCancellationRequested();
    }
}
