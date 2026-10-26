using InfoPanel.Enums;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Identifies one sensor reading across all sources. <see cref="Id"/> is source-specific:
    /// HWiNFO uses <c>{remote}:{id}:{instance}:{entry}</c>, Libre and plugins use their own sensor id.
    /// The string form is <c>hwinfo:-1:1:0:7</c>, <c>libre:/amdcpu/0/load/0</c> or
    /// <c>plugin:/infopanel.mock/gpu/gpu-temp</c>.
    /// </summary>
    public readonly record struct SensorKey(SensorType Type, string Id)
    {
        private const string HwInfoPrefix = "hwinfo";
        private const string LibrePrefix = "libre";
        private const string PluginPrefix = "plugin";

        public static SensorKey HwInfo(int remote, uint id, uint instance, uint entry)
        {
            return new SensorKey(SensorType.HwInfo, string.Create(CultureInfo.InvariantCulture, $"{remote}:{id}:{instance}:{entry}"));
        }

        public static SensorKey Libre(string id)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            return new SensorKey(SensorType.Libre, id);
        }

        public static SensorKey Plugin(string id)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            return new SensorKey(SensorType.Plugin, id);
        }

        public override string ToString()
        {
            var prefix = Type switch
            {
                SensorType.HwInfo => HwInfoPrefix,
                SensorType.Libre => LibrePrefix,
                SensorType.Plugin => PluginPrefix,
                _ => Type.ToString().ToLowerInvariant(),
            };

            return $"{prefix}:{Id}";
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out SensorKey key)
        {
            key = default;

            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            var separator = s.IndexOf(':');
            if (separator <= 0 || separator == s.Length - 1)
            {
                return false;
            }

            var prefix = s[..separator];
            var id = s[(separator + 1)..];

            switch (prefix)
            {
                case HwInfoPrefix:
                    {
                        var parts = id.Split(':');
                        if (parts.Length != 4
                            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var remote)
                            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sensorId)
                            || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var instance)
                            || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var entry))
                        {
                            return false;
                        }

                        key = HwInfo(remote, sensorId, instance, entry);
                        return true;
                    }
                case LibrePrefix:
                    key = Libre(id);
                    return true;
                case PluginPrefix:
                    key = Plugin(id);
                    return true;
                default:
                    return false;
            }
        }
    }
}
