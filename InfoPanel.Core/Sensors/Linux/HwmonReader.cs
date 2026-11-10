using Serilog;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// Finds the hwmon channels (<c>class/hwmon/hwmonN</c>): each device's <c>name</c> (its chip; in the older layout, with
/// no <c>name</c> in the hwmon folder, its attributes and channels are read on the physical device its <c>device</c> link
/// names, under the same ids), its anchor
/// (<see cref="HwmonAnchors"/>) and its <c>{kind}{N}_input</c> files, or <c>power{N}_average</c> where there is no
/// <c>power{N}_input</c> (an older amdgpu's), under the same <c>power{N}</c> id. Each channel is named by its
/// <c>{kind}{N}_label</c>, else its channel, and grouped by its chip and anchor. The kernel's units are scaled to °C
/// (millidegrees), RPM, V and A (milli), W (micro), MHz (from Hz) and % (milli).
/// </summary>
internal static partial class HwmonReader
{
    private static readonly FrozenDictionary<string, (string Unit, double Scale)> Units = new Dictionary<string, (string, double)>
    {
        ["temp"] = ("°C", 1_000),
        ["fan"] = ("RPM", 1),
        ["in"] = ("V", 1_000),
        ["curr"] = ("A", 1_000),
        ["power"] = ("W", 1_000_000),
        ["freq"] = ("MHz", 1_000_000),
        ["humidity"] = ("%", 1_000),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    [GeneratedRegex(@"^hwmon[0-9]+$")]
    private static partial Regex Entry();

    [GeneratedRegex(@"^(?<kind>temp|fan|in|curr|power|freq|humidity)(?<index>[0-9]+)_(?<file>input|average)$")]
    private static partial Regex ChannelFile();

    /// <summary>Adds every hwmon channel found to <paramref name="channels"/>; a device that can't be read is left out.</summary>
    public static void Scan(Sysfs sysfs, List<LinuxChannel> channels, ILogger log)
    {
        foreach (var entry in sysfs.List("class/hwmon"))
        {
            if (!Entry().IsMatch(entry))
                continue;
            try
            {
                ScanDevice(sysfs, entry, channels);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Linux sensors: {Device} can't be read and is left out", entry);
            }
        }
    }

    private static void ScanDevice(Sysfs sysfs, string entry, List<LinuxChannel> channels)
    {
        if (sysfs.RealPath("class/hwmon/" + entry) is not { } dir)
            return;
        var owner = sysfs.ReadLink(dir + "/device") != null ? sysfs.RealPath(dir + "/device") : null;

        // The attributes are in the hwmon folder, or, in the older layout, on the physical device its device link names.
        var attributes = dir;
        if (sysfs.ReadAttribute(dir + "/name") is not { } name)
        {
            if (owner == null || sysfs.ReadAttribute(owner + "/name") is not { } ownerName)
                return;
            (attributes, name) = (owner, ownerName);
        }

        var chip = HwmonAnchors.NormalizeChip(name);
        var (anchor, secondary) = HwmonAnchors.OfHwmon(sysfs, owner, chip);
        var shown = string.Join(' ', anchor.Tokens);
        var group = anchor.Kind == "name" || shown == chip ? chip : $"{chip} ({shown})";

        var files = new HashSet<string>(sysfs.List(attributes), StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (ChannelFile().Match(file) is not { Success: true } match)
                continue;
            var kind = match.Groups["kind"].Value;
            var index = match.Groups["index"].Value;
            if (match.Groups["file"].Value == "average" && (kind != "power" || files.Contains($"power{index}_input")))
                continue;

            var channel = kind + index;
            // A channel file is read where it really is, which must be within the root (a link out of it is no channel).
            if (!LinuxSensorIds.IsChannel(channel) || sysfs.RealPath($"{attributes}/{file}") is not { } path)
                continue;
            var label = sysfs.ReadAttribute($"{attributes}/{channel}_label") ?? channel;
            var (unit, scale) = Units[kind];
            channels.Add(new LinuxChannel(
                LinuxSensorIds.Hwmon(chip, anchor, secondary, channel), group, label, unit, path, scale,
                kind, int.Parse(index, CultureInfo.InvariantCulture), Alias: $"{entry}/{channel}"));
        }
    }
}
