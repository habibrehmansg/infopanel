using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// Jonsbo displays (family <c>jonsbo</c>), ported from InfoPanel 1.4: the DS916 on a USB CDC serial port
/// (<c>33c3:f101</c>; <see cref="Ds916Session"/>) and the DS339 on a MacroSilicon MS9132 (<c>345f:9132</c>;
/// <see cref="JonsboMs9132Session"/>). The MS9132 is the VMAX 4.6"'s bridge too (family <c>vmax</c>, in this package),
/// so the probe reads the EDID the bridge holds of its panel (<see cref="Ms9132Probe"/>) and the family answers by its
/// size: the DS339's 376 × 960 is its own; the VMAX's 320 × 960 is passed on to <see cref="VmaxDriver"/>, asked next;
/// any other size (or an EDID that doesn't read) is the unknown model, named by it (<c>edid-480x480</c>,
/// <c>edid-unread</c>), which starts once the family's <c>model</c> setting names the DS339 ("Try it as…"): 1.4's
/// Jonsbo section drove any MS9132 the user added to it, at its EDID's size when 1.4's mode table had it. The probe
/// gives no serial (the HID control interface reports none to the seam): panels are told apart by their port.
/// </summary>
public sealed class JonsboDriver : IPanelDriver
{
    /// <summary>The family's id, stored with every panel of it.</summary>
    public const string FamilyId = "jonsbo";

    /// <summary>The unknown model's id when the panel's EDID doesn't read.</summary>
    internal const string UnreadEdid = "edid-unread";

    private readonly IPanelDevices _devices;

    /// <summary>A driver that reaches its devices through <paramref name="devices"/>.</summary>
    public JonsboDriver(IPanelDevices devices) => _devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>1.4's pauses (tests: none); one ends early, throwing, when its token is cancelled.</summary>
    internal Action<TimeSpan, CancellationToken> Wait { get; init; } = Ms9132Link.Pause;

    /// <summary>The clock a probe keeps its budget by (tests: a scripted one).</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The most a frame's write takes (tests: a shorter one): <see cref="Ms9132Protocol.WriteBudget"/>.</summary>
    internal TimeSpan WriteBudget { get; init; } = Ms9132Protocol.WriteBudget;

    public string Family => FamilyId;

    public IReadOnlyList<PanelMatch> Matches => Ms9132JonsboManifest.Jonsbo.Matches;

    /// <summary>The unknown model's id for an EDID of <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal static string UnknownModel(int width, int height) => $"edid-{width}x{height}";

    public PanelProbe Probe(PanelCandidate device, CancellationToken stop)
    {
        var family = Ms9132JonsboManifest.Jonsbo;
        if (Ms9132JonsboManifest.ModelOf(family, device, "serial") is { } ds916)
            return Ds916Session.Probe(_devices, device, ds916, Time, Wait, stop);
        if (Ms9132JonsboManifest.ModelOf(family, device, "ms9132") is not { } ds339)
            return new(false, null, PanelDeviceStatus.Ok, "not a Jonsbo panel's USB id");

        var read = Ms9132Probe.Read(_devices, device, Time, Wait, stop);
        if (read.Status != PanelDeviceStatus.Ok)
            return new(false, null, read.Status, read.Message);
        if (!read.Answered)
            return new(false, null, PanelDeviceStatus.Ok, read.Message);
        if (read.Edid is { HasSize: true } edid)
        {
            if (Ms9132JonsboManifest.Ms9132ModelOf(Ms9132JonsboManifest.Vmax, edid.Width, edid.Height) is not null)
                return new(false, null, PanelDeviceStatus.Ok, $"a VMAX panel's EDID, the vmax family's: {read.Message}");
            if (Ms9132JonsboManifest.Ms9132ModelOf(family, edid.Width, edid.Height) is { } model)
                return new(true, new PanelIdentity(model.Id, model.Width, model.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok, read.Message);
            return new(true, new PanelIdentity(UnknownModel(edid.Width, edid.Height), edid.Width, edid.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok, read.Message);
        }
        // No size to go by: the DS339's, as 1.4's Jonsbo section drove such a panel.
        return new(true, new PanelIdentity(UnreadEdid, ds339.Width, ds339.Height, Serial: null, Firmware: null), PanelDeviceStatus.Ok, read.Message);
    }

    public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var family = Ms9132JonsboManifest.Jonsbo;
        if (Ms9132JonsboManifest.ModelOf(family, device, "serial") is { } ds916)
            return Ds916Session.Start(_devices, device, ds916, Time, Wait, WriteBudget, stop);
        if (Ms9132JonsboManifest.ModelOf(family, device, "ms9132") is null)
            return new(null, PanelDeviceStatus.NotSupported, "not a Jonsbo panel's USB id");
        if ((family.Model(identity.Model) ?? Ms9132JonsboManifest.TriedAs(family, options.FamilySettings)) is not { } model)
            return new(null, PanelDeviceStatus.NotSupported, $"the panel's EDID ({identity.Model}) names none of the family's models: try it as one of them");
        if (!Ms9132JonsboManifest.OnMs9132(model))
            return new(null, PanelDeviceStatus.NotSupported, $"the {model.Name} isn't driven over the MS9132: try it as the {Ms9132JonsboManifest.ModelOf(family, device, "ms9132")!.Name}");
        return JonsboMs9132Session.Start(_devices, device, model, Wait, WriteBudget, stop);
    }
}
