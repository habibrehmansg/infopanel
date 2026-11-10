using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// Finds the thermal zones (<c>class/thermal/thermal_zoneN</c>) that have a temperature: each under
/// <c>thermal/v1/type+{type}[~acpi+{path}]/temp</c> (<see cref="HwmonAnchors.OfZone"/>), named by its type, in
/// <see cref="Group"/>, read from its <c>temp</c> file in millidegrees.
/// </summary>
internal static partial class ThermalReader
{
    /// <summary>The group the thermal zones are listed in.</summary>
    public const string Group = "Thermal zones";

    [GeneratedRegex(@"^thermal_zone[0-9]+$")]
    private static partial Regex Entry();

    /// <summary>Adds every thermal zone found to <paramref name="channels"/>; a zone that can't be read is left out.</summary>
    public static void Scan(Sysfs sysfs, List<LinuxChannel> channels, ILogger log)
    {
        foreach (var entry in sysfs.List("class/thermal"))
        {
            if (!Entry().IsMatch(entry))
                continue;
            try
            {
                if (sysfs.RealPath("class/thermal/" + entry) is not { } dir
                    || sysfs.ReadAttribute(dir + "/type") is not { } type
                    || !sysfs.List(dir).Contains("temp")
                    || sysfs.RealPath(dir + "/temp") is not { } temp
                    || HwmonAnchors.OfZone(sysfs, dir) is not { } zone)
                {
                    continue;
                }

                var id = LinuxSensorIds.Thermal(zone.Anchor, zone.Secondary);
                channels.Add(new LinuxChannel(id, Group, type, "°C", temp, 1_000, LinuxSensorIds.ThermalChannel, 0, Alias: entry + "/temp"));
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Linux sensors: {Zone} can't be read and is left out", entry);
            }
        }
    }
}
