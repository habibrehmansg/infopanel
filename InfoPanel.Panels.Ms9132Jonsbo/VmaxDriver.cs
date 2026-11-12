using System.Text.Json;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// VMAX displays (family <c>vmax</c>), ported from InfoPanel 1.4: the VMAX 4.6" on a MacroSilicon MS9132
/// (<c>345f:9132</c>; <see cref="VmaxSession"/>), the bridge the Jonsbo DS339 shares. The engine asks the
/// <c>jonsbo</c> family first, which passes on a panel whose EDID gives the 4.6"'s 320 × 960; this probe reads the EDID
/// again (<see cref="Ms9132Probe"/>) and takes that size alone. Its family setting Screen on (<c>screenSwitch</c>,
/// 1.4's <c>ScreenSwitch</c>, on by default) is sent once frames flow.
/// </summary>
public sealed class VmaxDriver : IPanelDriver
{
    /// <summary>The family's id, stored with every panel of it.</summary>
    public const string FamilyId = "vmax";

    private readonly IPanelDevices _devices;

    /// <summary>A driver that reaches its devices through <paramref name="devices"/>.</summary>
    public VmaxDriver(IPanelDevices devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>1.4's pauses (tests: none); one ends early, throwing, when its token is cancelled.</summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; init; } = Ms9132Link.Pause;

    /// <summary>The clock of a probe's budget and of the status poll (tests: a scripted one).</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The most a frame's write takes (tests: a shorter one): <see cref="Ms9132Protocol.WriteBudget"/>.</summary>
    internal TimeSpan WriteBudget { get; init; } = Ms9132Protocol.WriteBudget;

    public string Family => FamilyId;

    public IReadOnlyList<PanelMatch> Matches => Ms9132JonsboManifest.Vmax.Matches;

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var family = Ms9132JonsboManifest.Vmax;
        if (Ms9132JonsboManifest.ModelOf(family, device, "ms9132") is null)
            return new(false, null, PanelDeviceStatus.Ok, "not a VMAX panel's USB id");
        var read = Ms9132Probe.Read(_devices, device, Time, Wait, stop);
        if (read.Status != PanelDeviceStatus.Ok)
            return new(false, null, read.Status, read.Message);
        if (read.Edid is { HasSize: true } edid && Ms9132JonsboManifest.Ms9132ModelOf(family, edid.Width, edid.Height) is { } model)
            return new(true, new PanelIdentity(model.Id, model.Width, model.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok, read.Message);
        return new(false, null, PanelDeviceStatus.Ok, read.Answered ? $"not a VMAX panel's EDID: {read.Message}" : read.Message);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var family = Ms9132JonsboManifest.Vmax;
        if (Ms9132JonsboManifest.ModelOf(family, device, "ms9132") is null)
            return new(null, PanelDeviceStatus.NotSupported, "not a VMAX panel's USB id");
        if (family.Model(identity.Model) is not { } model)
            return new(null, PanelDeviceStatus.NotSupported, $"{identity.Model} isn't one of the family's models");
        return VmaxSession.Start(_devices, device, model, ScreenOn(options.FamilySettings), Time, Wait, WriteBudget, stop);
    }

    /// <summary>The family setting Screen on, on unless it says false (1.4's default, Models/VmaxPanelDevice.cs:49-50).</summary>
    internal static bool ScreenOn(JsonElement settings) =>
        !(settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("screenSwitch", out var on) && on.ValueKind == JsonValueKind.False);
}
