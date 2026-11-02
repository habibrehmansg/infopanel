using InfoPanel.SensorHelper;
using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// One Libre sensor as the engine lists it, with what LibreHardwareMonitor says of it beyond the sensor list: its
/// <see cref="SensorType"/> (<c>Temperature</c>), its hardware's identifier, name and <see cref="HardwareType"/>
/// (<c>Cpu</c>, <c>SuperIO</c>), and the hardware it is listed under (<see cref="Group"/>: the topmost hardware it is
/// part of). Kept for a 1.x import that maps another tool's sensors by type (AIDA64's).
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
/// then sensor index); with its type's unit as the helper gives it (throughput in B/s).
/// </summary>
public sealed class LibreCatalogue
{
    private LibreCatalogue(long version, IReadOnlyList<LibreSensor> sensors, IReadOnlyList<SensorDefinition> definitions)
    {
        Version = version;
        Sensors = sensors;
        Definitions = definitions;
    }

    public static LibreCatalogue Empty { get; } = new(0, [], []);

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

        return new LibreCatalogue(answer.Version, sensors, definitions);
    }

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
