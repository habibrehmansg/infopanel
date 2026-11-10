using System.Collections.Generic;
using System.Threading;

namespace InfoPanel.Panels.Sdk;

/// <summary>
/// A family of USB panels: which devices it may drive (<see cref="Matches"/>), how it tells one of its own panels
/// (<see cref="Probe"/>), and how it starts one (<see cref="Open"/>). The engine lists the USB devices itself, with no I/O
/// to any of them, and offers a device only to the drivers whose matches name it, in a fixed order; the first whose probe
/// says "mine" owns the device until it is unplugged, and no other driver is asked about it. The engine renders,
/// transforms, encodes and paces the frames, keeps the panel's settings and identity, and decides when a session starts
/// and stops: a driver only talks to its device. A driver never retries: a failed call ends the session, and the engine
/// opens the device again on a later pass.
/// </summary>
public interface IPanelDriver
{
    /// <summary>The family's id, stored with every panel it finds (<c>beadapanel</c>): never renamed.</summary>
    string Family { get; }

    /// <summary>The devices it may drive, by USB vendor and product id; a device none of them names is never offered to it.</summary>
    IReadOnlyList<PanelMatch> Matches { get; }

    /// <summary>
    /// Whether <paramref name="device"/> is one of the family's panels, and which: it may open the device and query it, but
    /// never draws, and lets it go before it returns. "Not mine" (<see cref="PanelProbe.Mine"/> false, status
    /// <see cref="PanelDeviceStatus.Ok"/>) passes the device on to the next driver; a status other than <see cref="PanelDeviceStatus.Ok"/>
    /// means the device couldn't be asked now (another program holds it, say), and it is offered again on a later pass.
    /// <paramref name="stop"/> is cancelled when the engine stops, when the computer goes to sleep, and at the probe's
    /// deadline (a few seconds): a wait on the device ends with it (throwing <see cref="System.OperationCanceledException"/>
    /// is fine). A probe that throws, or that answers after its deadline, counts as one that couldn't ask the device.
    /// The probe may run on any thread. Callbacks registered on <paramref name="stop"/> must not throw or block: a driver
    /// whose callback does is contained (the failure logged, the device left until a later pass), not trusted.
    /// </summary>
    PanelProbe Probe(PanelCandidate device, CancellationToken stop);

    /// <summary>
    /// The session: the device opened and its start sequence run, the screen on, ready for frames; or, with no session, the
    /// status and why. A start that is cancelled throws <see cref="System.OperationCanceledException"/> with the device let
    /// go (its screen turned off again when the start had turned it on).
    /// </summary>
    PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop);
}
