using InfoPanel.Enums;
using System;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.HwInfo;

/// <summary>
/// The HWiNFO source: a <see cref="PolledSensorSource"/> with the id <c>hwinfo</c> over <see cref="HwInfoSampler"/>,
/// polled every 500 ms (as 1.x), answering every <c>hwinfo:</c> key (1.x's
/// <c>hwinfo:{remote}:{sensor id}:{instance}:{reading id}</c>, remote −1 for this PC). A key HWiNFO doesn't have
/// reads unavailable. Read as the normal user: opening HWiNFO's mapping for reading needs no administrator rights.
/// </summary>
public static class HwInfoSource
{
    public const string SourceId = "hwinfo";

    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    public static PolledSensorSourceOptions DefaultOptions => new() { Interval = Interval };

    /// <summary>HWiNFO doesn't run (or ran and ended): its shared memory isn't there.</summary>
    public static readonly SourceReason NotRunning = new("notRunning", "HWiNFO isn't running");

    /// <summary>HWiNFO runs, but its shared memory isn't there: Shared Memory Support is off.</summary>
    public static readonly SourceReason SharedMemoryOff = new("sharedMemoryOff", "HWiNFO's shared memory is off");

    /// <summary>HWiNFO's shared memory is there, but its poll time stopped: HWiNFO hangs, or the free version's 12 hours ended.</summary>
    public static readonly SourceReason Stopped = new("stopped", "HWiNFO stopped updating: turn Shared Memory Support on again");

    /// <summary>A source over <paramref name="opener"/>'s mappings (a recording's, a test's).</summary>
    public static PolledSensorSource Create(
        IHwInfoMemoryOpener opener,
        TimeProvider? time = null,
        PolledSensorSourceOptions? options = null,
        HwInfoSampler.IObserver? observer = null,
        string localName = HwInfoSampler.LocalName,
        string remotePrefix = HwInfoSampler.RemotePrefix,
        Func<bool>? hwinfoRunning = null) =>
        new(SourceId, () => new HwInfoSampler(opener, time, observer, localName, remotePrefix, hwinfoRunning: hwinfoRunning), options ?? DefaultOptions, time, SensorKeySpace.Of(SensorType.HwInfo));

    /// <summary>The source over this PC's HWiNFO, with its process check (<see cref="HwInfoProcess.IsRunning"/>).</summary>
    [SupportedOSPlatform("windows")]
    public static PolledSensorSource CreateWindows(TimeProvider? time = null, HwInfoSampler.IObserver? observer = null) =>
        Create(new WindowsHwInfoMemoryOpener(), time, null, observer, hwinfoRunning: HwInfoProcess.IsRunning);
}
