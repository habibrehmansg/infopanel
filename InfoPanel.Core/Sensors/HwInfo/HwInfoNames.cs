using InfoPanel.Enums;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace InfoPanel.Sensors.HwInfo;

/// <summary>
/// HWiNFO's readings as its source lists them now (<see cref="HwInfoSource"/>), looked up by key and by original name:
/// what a 1.x file's import rebinds a HWiNFO binding by when this PC doesn't have its key (1.x's "smart import").
/// </summary>
public sealed class HwInfoNames
{
    /// <summary>One reading: its key's parts, its original name and its sensor's.</summary>
    public sealed record Reading(SensorKey Key, int Remote, uint SensorId, uint Instance, uint ReadingId, string Name, string Sensor);

    private readonly HashSet<SensorKey> _keys = [];
    // The first reading of each name, and of each name in each sensor id, indexed once: a lookup is two probes however
    // many readings share a name.
    private readonly Dictionary<(int Remote, string Name), Reading> _firstByName = [];
    private readonly Dictionary<(int Remote, string Name, uint SensorId), Reading> _firstBySensor = [];

    private HwInfoNames()
    {
    }

    /// <summary>The HWiNFO source's readings among <paramref name="sensors"/>, in their order (HWiNFO's).</summary>
    public static HwInfoNames Of(IEnumerable<SensorInfo> sensors)
    {
        var names = new HwInfoNames();
        foreach (var sensor in sensors)
        {
            if (sensor.SourceId != HwInfoSource.SourceId || sensor.Key.Type != SensorType.HwInfo || !TryParts(sensor.Key, out var remote, out var id, out var instance, out var reading))
                continue;
            if (!names._keys.Add(sensor.Key))
                continue;

            var entry = new Reading(sensor.Key, remote, id, instance, reading, sensor.Name, sensor.Group);
            names._firstByName.TryAdd((remote, sensor.Name), entry);
            names._firstBySensor.TryAdd((remote, sensor.Name, id), entry);
        }
        return names;
    }

    /// <summary>How many lookups <see cref="Match"/> has made so far: what bounds an import's cost, counted for its tests.</summary>
    internal long Probes { get; private set; }

    /// <summary>Whether HWiNFO lists <paramref name="key"/>.</summary>
    public bool Has(SensorKey key) => _keys.Contains(key);

    /// <summary>
    /// The reading a binding to <paramref name="remote"/>'s sensor <paramref name="sensorId"/> named
    /// <paramref name="name"/> goes to: on the same remote, with exactly that original name; among several, the first in
    /// a sensor with the binding's sensor id, else the first in HWiNFO's order. Null when there is none.
    /// </summary>
    public Reading? Match(int remote, uint sensorId, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        Probes++;
        if (_firstBySensor.TryGetValue((remote, name, sensorId), out var inSensor))
            return inSensor;
        Probes++;
        return _firstByName.GetValueOrDefault((remote, name));
    }

    private static bool TryParts(SensorKey key, out int remote, out uint id, out uint instance, out uint reading)
    {
        remote = 0;
        id = instance = reading = 0;
        var parts = key.Id.Split(':');
        return parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out remote)
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out instance)
            && uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out reading);
    }
}
