using System;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>
/// The macOS sensor source: a <see cref="PolledSensorSource"/> over <see cref="MacSensorSampler"/>, polled once a
/// second on the thread pool. Keys are <c>plugin:/infopanel.macos/{group}/{sensor}</c> (see
/// <c>docs/2.0/decisions.md</c> for the catalogue). Everything is read as the normal user, without root or extra privileges;
/// what the OS refuses reads as <see cref="SensorStatus.PermissionDenied"/>, what isn't there is not listed or reads as
/// <see cref="SensorStatus.Unavailable"/>.
/// </summary>
public static class MacSensorSource
{
    public const string SourceId = "infopanel.macos";

    public const string KeyPrefix = "/" + SourceId + "/";

    /// <summary>The key of a catalogue sensor by its id (<c>cpu/load</c> → <c>plugin:/infopanel.macos/cpu/load</c>).</summary>
    public static SensorKey KeyOf(string id) => SensorKey.Plugin(KeyPrefix + id);

    [SupportedOSPlatform("macos")]
    public static PolledSensorSource Create(TimeProvider? time = null, PolledSensorSourceOptions? options = null) =>
        new(SourceId, () => new MacSensorSampler(), options, time);
}
