using InfoPanel.SensorHelper;
using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// One Libre sensor as the engine lists it, with what LibreHardwareMonitor says of it beyond the sensor list: its
/// <see cref="SensorType"/> (<c>Temperature</c>), its hardware's identifier, name and <see cref="HardwareType"/>
/// (<c>Cpu</c>, <c>SuperIO</c>), and the hardware it is listed under (<see cref="Group"/>: the topmost hardware it is
/// part of). Kept for a 1.x import that maps another tool's sensors by type (AIDA64's). A top-level hardware's name is
/// listed here too, as a text: <see cref="SensorType"/> <c>Name</c>, <see cref="Index"/> −1, which such a mapping skips.
/// </summary>
public sealed record LibreSensor(
    string Identifier,
    string Name,
    string SensorType,
    int Index,
    string Unit,
    string Hardware,
    string HardwareName,
    string HardwareType,
    string Group);

/// <summary>
/// The helper's catalogue as the Libre source lists it (<see cref="Of"/>): each sensor under the key
/// <c>libre:{Identifier}</c>, its identifier exactly as LibreHardwareMonitor gives it (and as 1.4.x stored it); named
/// as LibreHardwareMonitor names it; grouped under its hardware's name, sub-hardware folded into the hardware it is part
/// of (a board's Super I/O chip under the board, as 1.4.x listed it); in the helper's order, 1.4.x's (by hardware type,
/// then sensor index); with its type's unit as the helper gives it (throughput in B/s). After the helper's sensors, each
/// top-level hardware's name (sub-hardware has none), in the helper's hardware order: a text sensor
/// <c>libre:{hardware Identifier}/name</c>, named <c>Name</c>, in the hardware's group, with no unit; so the helper's
/// values keep their places (<see cref="HelperCount"/>).
/// </summary>
public sealed class LibreCatalogue
{
    private LibreCatalogue(long version, IReadOnlyList<LibreSensor> sensors, IReadOnlyList<SensorDefinition> definitions, int helperCount)
    {
        Version = version;
        Sensors = sensors;
        Definitions = definitions;
        HelperCount = helperCount;
    }

    public static LibreCatalogue Empty { get; } = new(0, [], [], 0);

    /// <summary>The helper's own sensors, the first ones listed; the hardware names follow them.</summary>
    public int HelperCount { get; }

    /// <summary>The helper's version of it (0 for none).</summary>
    public long Version { get; }

    public IReadOnlyList<LibreSensor> Sensors { get; }

    /// <summary>The sensors as a polled source lists them, in the same order.</summary>
    public IReadOnlyList<SensorDefinition> Definitions { get; }

    public int Count => Sensors.Count;

    /// <summary>The catalogue of <paramref name="answer"/> (which the protocol's codec checked: identifiers, caps, no parent cycles).</summary>
    public static LibreCatalogue Of(CatalogueAnswer answer)
    {
        var hardware = new Dictionary<string, HelperHardware>(StringComparer.Ordinal);
        foreach (var h in answer.Hardware)
            hardware.TryAdd(h.Id, h);

        var sensors = new List<LibreSensor>(answer.Sensors.Count);
        var definitions = new List<SensorDefinition>(answer.Sensors.Count);
        foreach (var s in answer.Sensors)
        {
            hardware.TryGetValue(s.Hw, out var own);
            var group = GroupOf(own, hardware);
            var sensor = new LibreSensor(s.Id, s.Name, s.Type, s.Index, s.Unit, s.Hw, own?.Name ?? "", own?.Type ?? "", group);
            sensors.Add(sensor);
            definitions.Add(new SensorDefinition(s.Id, group, s.Name, s.Unit));
        }

        // Each top-level hardware's name (part of no hardware listed: its own group), once, after the helper's sensors.
        var helperCount = sensors.Count;
        foreach (var h in answer.Hardware)
        {
            if ((h.Parent is { } parent && hardware.ContainsKey(parent)) || !ReferenceEquals(hardware[h.Id], h))
                continue;
            var id = h.Id + "/name";
            sensors.Add(new LibreSensor(id, NameSensor, NameSensor, -1, "", h.Id, h.Name, h.Type, h.Name));
            definitions.Add(new SensorDefinition(id, h.Name, NameSensor, "", SensorKind.Text));
        }

        return new LibreCatalogue(answer.Version, sensors, definitions, helperCount);
    }

    /// <summary>A hardware name's sensor name, and its sensor type in <see cref="LibreSensor"/>.</summary>
    private const string NameSensor = "Name";

    /// <summary>The name of the topmost hardware <paramref name="own"/> is part of (itself when it is part of none).</summary>
    private static string GroupOf(HelperHardware? own, Dictionary<string, HelperHardware> hardware)
    {
        if (own is null)
            return "";

        var top = own;
        // The codec refuses parents that go round, so this ends; the bound only guards against a catalogue built elsewhere.
        for (var steps = 0; steps < hardware.Count && top.Parent is { } parent && hardware.TryGetValue(parent, out var up); steps++)
            top = up;
        return top.Name;
    }
}
