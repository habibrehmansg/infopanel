using System.Text.Json;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// The package's one table, <c>panel.json</c> beside the driver's assembly: both families' matches and models, each model
/// with its USB id (<c>usb</c>), its transport (<c>transport</c>) and, for a serial one, its revision (<c>revision</c>:
/// A, C or E; the XuanFang's names none the driver drives).
/// </summary>
internal static class TuringCatalog
{
    private static readonly Lazy<PanelManifest> Manifest = new(() => PanelManifest.Read(typeof(TuringCatalog).Assembly));

    public static PanelManifestFamily Turing => Manifest.Value.Family(TuringDriver.FamilyId);

    public static PanelManifestFamily LianLi => Manifest.Value.Family(LianLiDriver.FamilyId);

    /// <summary>The models of <paramref name="family"/> on <paramref name="device"/>'s USB id, in the table's order (the 3.5"'s port has two).</summary>
    public static IReadOnlyList<PanelManifestModel> ModelsOf(PanelManifestFamily family, PanelCandidate device)
    {
        var usb = $"{device.VendorId:x4}:{device.ProductId:x4}";
        return [.. family.Models.Where(m => Text(m, "usb") == usb)];
    }

    /// <summary>A model's own string member, or null.</summary>
    public static string? Text(PanelManifestModel model, string name) =>
        model.Json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool IsSerial(PanelManifestModel model) => Text(model, "transport") == "serial";

    /// <summary>The <c>model</c> family setting ("Try it as…"), or null when none is set.</summary>
    public static string? TriedModel(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
        && model.GetString() is { Length: > 0 } id ? id : null;

    /// <summary>The device's cached descriptors say it has interface 0 (1.4 claimed it, <c>LianLiUsbScreenDevice.cs:56-60</c>).</summary>
    public static PanelProbe ProbeUsb(IPanelDevices devices, PanelCandidate device, PanelManifestModel model)
    {
        var status = devices.Describe(device, out var descriptor);
        if (status != PanelDeviceStatus.Ok)
            return new(false, null, status, "its descriptors couldn't be read");
        if (descriptor == null || descriptor.Interfaces.All(i => i.Number != 0))
            return new(false, null, PanelDeviceStatus.Ok, "it has no interface 0");
        return new(true, new PanelIdentity(model.Id, model.Width, model.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok);
    }

    /// <summary>A session's start: its session, or its status and why, the device let go; a cancel lets it go and throws.</summary>
    public static PanelOpen Started(IPanelSession session, Func<PanelDeviceStatus> start, Func<string> why)
    {
        try
        {
            var status = start();
            if (status == PanelDeviceStatus.Ok)
                return new(session, status, "");
            session.Close(screenOff: true, StartCloseBudget);
            session.Dispose();
            return new(null, status, why());
        }
        catch (OperationCanceledException)
        {
            session.Close(screenOff: true, StartCloseBudget);
            session.Dispose();
            throw;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>The time a failed or cancelled start gives its screen-off: the engine's bound for a stop.</summary>
    public static readonly TimeSpan StartCloseBudget = TimeSpan.FromSeconds(3);

    /// <summary>How long an open may take.</summary>
    public const int OpenTimeoutMs = 5000;

    /// <summary>A pause, ended early (throwing) by its token.</summary>
    public static void Pause(TimeSpan time, CancellationToken stop)
    {
        if (time <= TimeSpan.Zero)
        {
            stop.ThrowIfCancellationRequested();
            return;
        }
        if (stop.CanBeCanceled)
            stop.WaitHandle.WaitOne(time);
        else
            Thread.Sleep(time);
        stop.ThrowIfCancellationRequested();
    }
}
