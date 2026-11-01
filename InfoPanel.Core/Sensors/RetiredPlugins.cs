using System;
using System.Collections.Generic;
using InfoPanel.Enums;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Drives;
using InfoPanel.Sensors.Network;

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
        ["drive-info-plugin"] = DriveSource.Name,
        ["network-info-plugin"] = NetworkSource.Name,
    };

    // A retired plugin's key id (1.x's PluginSensorId) → what it is bound to on import, for the plugins whose keys are
    // fixed (the clock's); a drive's and the network's keys follow a rule (DriveKey, NetworkKey).
    private static readonly Dictionary<string, Translation> Keys = Build();

    private const string DriveInfoPrefix = "/drive-info-plugin/";
    private const string NetworkInfoPrefix = "/network-info-plugin/";

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

    /// <summary>
    /// Every fixed key a 1.x import binds (the Clock plugin's), and what it binds it to. The Drive Info plugin's keys,
    /// one set per drive, and the Network Info plugin's follow a rule instead (<see cref="Translate"/>).
    /// </summary>
    public static IEnumerable<KeyValuePair<SensorKey, Translation>> All
    {
        get
        {
            foreach (var (id, translation) in Keys)
                yield return new(SensorKey.Plugin(id), translation);
        }
    }

    /// <summary>
    /// What a 1.x import binds <paramref name="key"/> to: the native entry for a retired plugin's key, else null (any
    /// other key, and an entry no source has). The Clock plugin's eight keys go to the clock's; the Drive Info plugin's
    /// <c>/drive-info-plugin/{drive}/{entry}</c>, for any drive id the plugin could make (<see cref="DriveSource.IsDriveId"/>)
    /// and each of its entries, to the drives source's <c>plugin:/infopanel.drives/{drive}/{entry}</c>, whether or not
    /// this computer has the drive. The Network Info plugin's public addresses,
    /// <c>/network-info-plugin/public-ip/{ipv4|ipv6}</c>, go to the network source's <c>plugin:/infopanel.network/public/{ipv4|ipv6}</c>;
    /// an interface's <c>/network-info-plugin/{interface}/{entry}</c>, for each of its entries
    /// (<see cref="NetworkSource.EntryName"/>), to <c>plugin:/infopanel.network/{id}/{entry}</c>, where the id is the
    /// SDK's id of the stored one (<see cref="NetworkSource.ContainerOf"/>), whether or not this computer has the
    /// interface; an interface whose id makes none, or makes the public addresses', isn't bound.
    /// </summary>
    public static Translation? Translate(SensorKey key)
    {
        if (key.Type != SensorType.Plugin)
            return null;
        return Keys.TryGetValue(key.Id, out var translation) ? translation : DriveKey(key.Id) ?? NetworkKey(key.Id);
    }

    private static Translation? NetworkKey(string id)
    {
        if (!id.StartsWith(NetworkInfoPrefix, StringComparison.Ordinal))
            return null;
        var rest = id[NetworkInfoPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            return null;
        var stored = rest[..slash];
        var entry = rest[(slash + 1)..];
        // The public addresses: the container "Public IP" (public-ip), its texts IPv4 and IPv6.
        if (stored == "public-ip")
        {
            foreach (var (publicId, publicName) in NetworkSource.PublicEntries)
            {
                if (entry == publicId)
                    return new Translation(NetworkSource.PublicKeyOf(publicId), "Network Info", NetworkSource.Name, $"{publicName} of {NetworkSource.PublicGroup}");
            }

            return null;
        }

        var container = NetworkSource.ContainerOf(stored);
        if (container.Length == 0 || container == NetworkSource.PublicContainer || NetworkSource.EntryName(entry) is not { } name)
            return null;
        // Addresses by position: the first is 1 as a person counts them.
        var shown = entry.StartsWith("ipv", StringComparison.Ordinal) ? $"{name} {int.Parse(entry[5..], System.Globalization.CultureInfo.InvariantCulture) + 1}" : name;
        return new Translation(NetworkSource.KeyOf(container, entry), "Network Info", NetworkSource.Name, $"{shown} of {stored}");
    }

    private static Translation? DriveKey(string id)
    {
        if (!id.StartsWith(DriveInfoPrefix, StringComparison.Ordinal))
            return null;
        var rest = id[DriveInfoPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0)
            return null;
        var drive = rest[..slash];
        var entry = rest[(slash + 1)..];
        if (!DriveSource.IsDriveId(drive) || DriveSource.EntryName(entry) is not { } name)
            return null;
        return new Translation(DriveSource.KeyOf(drive, entry), "Drive Info", DriveSource.Name, $"{name} of {Shown(drive)}");
    }

    /// <summary>A drive id as a person knows the drive: a letter as Windows writes it (<c>c</c> → <c>C:</c>), the root as <c>/</c>.</summary>
    private static string Shown(string drive) => drive switch
    {
        "" => "/",
        [var letter and >= 'a' and <= 'z'] => $"{char.ToUpperInvariant(letter)}:",
        _ => drive,
    };
}
