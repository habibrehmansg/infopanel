using System;
using System.Collections.Generic;
using InfoPanel.Enums;
using InfoPanel.Sensors.Clock;

namespace InfoPanel.Sensors;

/// <summary>
/// The 1.x plugins whose sensors the engine's own sources read now, under keys of their own: the plugins source refuses
/// a package's plugin with such an id (an old copy of a bundled package), so their keys don't come back half live, and
/// a 1.x import binds each of their keys that a source has an entry for to that entry's key, once
/// (<see cref="Translate"/>). The engine never answers their keys: a binding to one that wasn't imported reads
/// unavailable.
/// </summary>
public static class RetiredPlugins
{
    /// <summary>What a retired plugin's key is bound to on import: the source's key, the plugin's and the source's names, the entry's name.</summary>
    public sealed record Translation(SensorKey Key, string Plugin, string Source, string Name);

    // Plugin id → the name of the source that reads its sensors now.
    private static readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clock-plugin"] = ClockSensorSource.Name,
    };

    // A retired plugin's key id (1.x's PluginSensorId) → what it is bound to on import.
    private static readonly Dictionary<string, Translation> Keys = Build();

    private static Dictionary<string, Translation> Build()
    {
        var keys = new Dictionary<string, Translation>(StringComparer.Ordinal);
        // The 1.x Clock plugin: one container, "Clock" (clock), its entries the source's, one to one.
        foreach (var (id, name) in ClockSensorSource.Entries)
            keys[$"/clock-plugin/clock/{id}"] = new Translation(ClockSensorSource.KeyOf(id), "Clock", ClockSensorSource.Name, name);
        return keys;
    }

    /// <summary>The name of the source that reads the retired plugin <paramref name="pluginId"/>'s sensors now (any case), or null.</summary>
    public static string? SourceOf(string pluginId) => Sources.TryGetValue(pluginId, out var source) ? source : null;

    /// <summary>Every key a 1.x import binds, and what it binds it to.</summary>
    public static IEnumerable<KeyValuePair<SensorKey, Translation>> All
    {
        get
        {
            foreach (var (id, translation) in Keys)
                yield return new(SensorKey.Plugin(id), translation);
        }
    }

    /// <summary>What a 1.x import binds <paramref name="key"/> to: the native entry for a retired plugin's key, else null (any other key, and an entry no source has).</summary>
    public static Translation? Translate(SensorKey key) =>
        key.Type == SensorType.Plugin && Keys.TryGetValue(key.Id, out var translation) ? translation : null;
}
