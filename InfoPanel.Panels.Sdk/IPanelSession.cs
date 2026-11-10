using System;
using System.Text.Json;
using System.Threading;

namespace InfoPanel.Panels.Sdk;

/// <summary>
/// One running panel, from its start (<see cref="IPanelDriver.Open"/>) to its stop (<see cref="Close"/>). One thread
/// drives it, and its calls block; a call that doesn't answer <see cref="PanelDeviceStatus.Ok"/> ends the session.
/// </summary>
public interface IPanelSession : IDisposable
{
    /// <summary>What the panel takes, fixed for the session's life.</summary>
    PanelCapabilities Capabilities { get; }

    /// <summary>
    /// Writes one frame, in the first of <see cref="PanelCapabilities.Formats"/> the engine produces. The answer's
    /// <see cref="PanelWrite.DeviceTime"/> is the time the transfer alone took, measured by the driver around its write:
    /// the engine paces the panel by it.
    /// </summary>
    PanelWrite WriteFrame(PanelFrameView frame, CancellationToken stop);

    /// <summary>The brightness, 0–100; the driver maps it to its device's range. Sent between frames, when it changes.</summary>
    PanelDeviceStatus SetBrightness(int percent);

    /// <summary>Blanks the screen (or lights it again) without ending the session; without it, the engine stops the session and opens it again.</summary>
    PanelDeviceStatus SetPower(bool on) => PanelDeviceStatus.NotSupported;

    /// <summary>A protocol's own ping; without it, the engine writes the last frame again after <see cref="PanelCapabilities.KeepAliveEvery"/>.</summary>
    PanelDeviceStatus KeepAlive() => PanelDeviceStatus.NotSupported;

    /// <summary>The family's own settings, already checked by the engine.</summary>
    PanelDeviceStatus Apply(JsonElement familySettings) => PanelDeviceStatus.Ok;

    /// <summary>
    /// Ends the session and lets the device go. With <paramref name="screenOff"/>, the screen is turned off first: every
    /// step of it is tried (but none once the device is gone), all within <paramref name="budget"/> from the call (a write
    /// isn't started once it is spent). Without it (the device is gone), nothing is written.
    /// </summary>
    void Close(bool screenOff, TimeSpan budget);
}
