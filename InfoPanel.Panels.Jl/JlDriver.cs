using System.Text.Json;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Jl;

/// <summary>
/// Jungle Leopard / Hongtai cooler displays (family <c>jl</c>): the Hongtai reference firmware on a USB CDC serial port
/// (<c>33c3:7788</c>), ported from InfoPanel 1.4 (<see cref="JlProtocol"/>, <see cref="JlSession"/>). The port is asked for
/// 1.4's 2,000,000 baud (a CDC port ignores the rate), and for the nominal 115,200 when a platform refuses that. The probe,
/// within <see cref="ProbeBudget"/> on one clock, asks the device info alone; only when that goes unanswered for its whole
/// <see cref="ProbeReplyMs"/> does it send 1.4's pipeline reset and ask again; a device that answers neither is not the
/// family's ("not mine", which the engine keeps until the device is plugged in again: the driver remembers nothing). A
/// probe that couldn't ask (the port not opened, a call failed, its time run out) answers a status, and the device is
/// offered again later. The device info's resolution names the model (<c>panel.json</c>'s sizes: the Chill Arc 360,
/// 480 × 960; the Strip, 1920 × 462); a resolution the table hasn't is the unknown model, named by its size, which starts
/// once the family's <c>model</c> setting names one of the family's models ("Try it as…"); a reply without a size is the
/// model 1.4's table gave the USB id. A session runs at the size the device info gives at its start, as 1.4 trusted it over
/// its table. The SPI variant (<c>0x7793</c>) is a different transport, deliberately not driven.
/// </summary>
public sealed class JlDriver : IPanelDriver
{
    /// <summary>The family's id, stored with every panel of it.</summary>
    public const string FamilyId = "jl";

    /// <summary>How long the port's open may take.</summary>
    internal const int OpenTimeoutMs = 5000;

    /// <summary>The time a failed or cancelled start gives its close: the engine's bound for a stop.</summary>
    internal static readonly TimeSpan StartCloseBudget = TimeSpan.FromSeconds(3);

    private static readonly Lazy<PanelManifestFamily> Manifest = new(() => PanelManifest.Read(typeof(JlDriver).Assembly).Family(FamilyId));

    private readonly IPanelDevices? _devices;

    /// <summary>A driver without the device seam: its probe answers <see cref="PanelDeviceStatus.NotSupported"/> and it opens nothing.</summary>
    public JlDriver()
    {
    }

    /// <summary>A driver that reaches its devices through <paramref name="devices"/>.</summary>
    public JlDriver(IPanelDevices devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>1.4's pauses at the open (tests: none); one ends early, throwing, when its token is cancelled.</summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; init; } = Pause;

    /// <summary>The probe's whole time, inside the engine's 5 s deadline with room for the port's dispose and the answer's way back.</summary>
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(4);

    /// <summary>How long the probe waits for each answer to the device info query.</summary>
    internal const int ProbeReplyMs = 1000;

    /// <summary>The clock the probe keeps its budget by (tests: a scripted one).</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    public string Family => FamilyId;

    public IReadOnlyList<PanelMatch> Matches => Manifest.Value.Matches;

    /// <summary>The first model whose USB id is <paramref name="device"/>'s (1.4's lookup by VID and PID), or null.</summary>
    internal static PanelManifestModel? ModelOf(PanelCandidate device)
    {
        var usb = $"{device.VendorId:x4}:{device.ProductId:x4}";
        return Manifest.Value.Models.FirstOrDefault(m => m.Json.TryGetProperty("usb", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == usb);
    }

    /// <summary>The model of a panel <paramref name="width"/> × <paramref name="height"/>, or null.</summary>
    internal static PanelManifestModel? ModelOf(int width, int height) =>
        Manifest.Value.Models.FirstOrDefault(m => m.Width == width && m.Height == height);

    /// <summary>The unknown model's id for a resolution the table hasn't: <c>{width}x{height}</c>.</summary>
    internal static string UnknownModel(int width, int height) => $"{width}x{height}";

    /// <summary>A model's frame cap: its <c>maxJpegBytes</c>, else 80 KB.</summary>
    private static int MaxFrameBytes(PanelManifestModel model) =>
        model.Json.TryGetProperty("maxJpegBytes", out var cap) && cap.ValueKind == JsonValueKind.Number && cap.TryGetInt32(out var bytes) && bytes > 0
            ? bytes : JlSession.DefaultMaxFrameBytes;

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        if (ModelOf(device) is not { } byUsb)
            return new(false, null, PanelDeviceStatus.Ok, "not a JL panel's USB id");
        if (_devices == null)
            return new(false, null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        // The whole probe keeps to one budget, its clock read once here: the open, the pauses, the writes and both waits
        // for an answer each get no more than what is left of it.
        var started = Time.GetTimestamp();
        int Left() => (int)Math.Clamp(Math.Floor((ProbeBudget - Time.GetElapsedTime(started)).TotalMilliseconds), 0, IPanelDevices.MaxTimeoutMs);

        var status = Connect(_devices, device, Left, stop, out var session);
        if (session == null)
            return new(false, null, status, "the panel's serial port couldn't be opened");
        JlDeviceInfo? info;
        string why;
        try
        {
            status = session.Probe(Left, ProbeReplyMs, stop, out info, out why);
        }
        finally
        {
            session.Dispose(); // the probe only asked: nothing to close
        }
        if (status != PanelDeviceStatus.Ok)
            return new(false, null, status, why);
        if (info == null)
            return new(false, null, PanelDeviceStatus.Ok, why); // asked, twice, and no answer: not one of the family's
        return new(true, Identify(info, byUsb), PanelDeviceStatus.Ok);
    }

    /// <summary>
    /// The identity a device info gives: the model by its resolution, the unknown model named by a resolution the table
    /// hasn't, the USB id's model when it gives no size; its <c>uid</c> as the serial and <c>version</c> as the firmware.
    /// </summary>
    private static PanelIdentity Identify(JlDeviceInfo info, PanelManifestModel byUsb)
    {
        string model;
        int width, height;
        if (info.Width > 0 && info.Height > 0)
            (model, width, height) = (ModelOf(info.Width, info.Height)?.Id ?? UnknownModel(info.Width, info.Height), info.Width, info.Height);
        else
            (model, width, height) = (byUsb.Id, byUsb.Width, byUsb.Height);
        return new PanelIdentity(model, width, height, Text(info.Uid), Text(info.Version));
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The model the family setting <c>model</c> names ("Try it as…"), or null.</summary>
    private static PanelManifestModel? TriedAs(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
            ? Manifest.Value.Model(model.GetString()!) : null;

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (ModelOf(device) is null)
            return new(null, PanelDeviceStatus.NotSupported, "not a JL panel's USB id");
        if (_devices == null)
            return new(null, PanelDeviceStatus.NotSupported, "this driver was made without the device seam");
        if ((Manifest.Value.Model(identity.Model) ?? TriedAs(options.FamilySettings)) is not { } model)
            return new(null, PanelDeviceStatus.NotSupported, $"the panel's resolution, {identity.Model}, isn't one of the family's models: try it as one of them");

        var status = Connect(_devices, device, () => OpenTimeoutMs, stop, out var session);
        if (session == null)
            return new(null, status, "the panel's serial port couldn't be opened");
        try
        {
            status = session.Hello(stop, out var info, out var why);
            if (status == PanelDeviceStatus.Ok)
            {
                // 1.4 trusted the size the device gives over its table's (JlPanelDeviceTask.cs:77-82).
                var (width, height) = info!.Width > 0 && info.Height > 0 ? (info.Width, info.Height) : (model.Width, model.Height);
                status = session.Begin(width, height, MaxFrameBytes(model), options.Brightness, stop, out why);
                if (status == PanelDeviceStatus.Ok)
                    return new(session, status, "");
            }
            // 1.4 disposed the device after a failed start, and its dispose sent close.
            session.Close(screenOff: true, StartCloseBudget);
            return new(null, status, why);
        }
        catch (OperationCanceledException)
        {
            session.Close(screenOff: true, StartCloseBudget);
            throw;
        }
    }

    /// <summary>
    /// The device's serial port at 1.4's rate, else at the nominal one when the platform refuses that, each open given what
    /// <paramref name="timeoutMs"/> answers then (none left: <see cref="PanelDeviceStatus.Timeout"/>); a session over it.
    /// </summary>
    private PanelDeviceStatus Connect(IPanelDevices devices, PanelCandidate device, Func<int> timeoutMs, CancellationToken stop, out JlSession? session)
    {
        session = null;
        IPanelSerial? serial = null;
        var status = timeoutMs() is var first and > 0
            ? devices.OpenSerial(device, new PanelSerialOptions(JlProtocol.Baud, Dtr: true, Rts: true), first, out serial, stop)
            : PanelDeviceStatus.Timeout;
        if (serial == null && status == PanelDeviceStatus.Io && timeoutMs() is var second and > 0)
            status = devices.OpenSerial(device, new PanelSerialOptions(JlProtocol.FallbackBaud, Dtr: true, Rts: true), second, out serial, stop);
        if (serial == null)
            return status == PanelDeviceStatus.Ok ? PanelDeviceStatus.Io : status;
        session = new JlSession(serial, Wait, Time);
        return PanelDeviceStatus.Ok;
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
