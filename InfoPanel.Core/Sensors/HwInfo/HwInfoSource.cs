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

    /// <summary>A source over <paramref name="opener"/>'s mappings (a recording's, a test's).</summary>
    public static PolledSensorSource Create(
        IHwInfoMemoryOpener opener,
        TimeProvider? time = null,
        PolledSensorSourceOptions? options = null,
        HwInfoSampler.IObserver? observer = null,
        string localName = HwInfoSampler.LocalName,
        string remotePrefix = HwInfoSampler.RemotePrefix) =>
        new(SourceId, () => new HwInfoSampler(opener, time, observer, localName, remotePrefix), options ?? DefaultOptions, time, SensorKeySpace.Of(SensorType.HwInfo));

    /// <summary>The source over this PC's HWiNFO.</summary>
    [SupportedOSPlatform("windows")]
    public static PolledSensorSource CreateWindows(TimeProvider? time = null, HwInfoSampler.IObserver? observer = null) =>
        Create(new WindowsHwInfoMemoryOpener(), time, null, observer);
}
