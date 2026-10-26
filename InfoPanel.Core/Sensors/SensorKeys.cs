using InfoPanel.Enums;
using InfoPanel.Models;
using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Maps between the 1.x sensor binding properties on display items and <see cref="SensorKey"/>.
    /// </summary>
    public static class SensorKeys
    {
        /// <summary>
        /// The sensor an item is bound to, or null if it is not sensor-bound or the binding is empty.
        /// </summary>
        public static SensorKey? Of(DisplayItem item)
        {
            switch (item)
            {
                case ISensorItem sensorItem:
                    {
                        // ISensorItem inherits SensorType from all three binding interfaces; read it through one.
                        IHwInfoSensorItem hwInfo = sensorItem;
                        ILibreSensorItem libre = sensorItem;
                        IPluginSensorItem plugin = sensorItem;

                        return hwInfo.SensorType switch
                        {
                            SensorType.HwInfo => SensorKey.HwInfo(hwInfo.HwInfoRemoteIndex, hwInfo.Id, hwInfo.Instance, hwInfo.EntryId),
                            SensorType.Libre => string.IsNullOrEmpty(libre.LibreSensorId) ? null : SensorKey.Libre(libre.LibreSensorId),
                            SensorType.Plugin => string.IsNullOrEmpty(plugin.PluginSensorId) ? null : SensorKey.Plugin(plugin.PluginSensorId),
                            _ => null,
                        };
                    }
                // Tables only ever read plugin sensors, whatever their SensorType says (as in 1.x).
                case IPluginSensorItem pluginSensorItem:
                    return string.IsNullOrEmpty(pluginSensorItem.PluginSensorId) ? null : SensorKey.Plugin(pluginSensorItem.PluginSensorId);
                default:
                    return null;
            }
        }

        /// <summary>
        /// The item properties that bind an item to <paramref name="key"/>, by property name.
        /// </summary>
        public static IReadOnlyDictionary<string, object> ToBindingProps(SensorKey key, string sensorName)
        {
            var props = new Dictionary<string, object>
            {
                ["SensorName"] = sensorName,
                ["SensorType"] = key.Type,
            };

            switch (key.Type)
            {
                case SensorType.HwInfo:
                    {
                        var parts = key.Id.Split(':');
                        if (parts.Length != 4)
                        {
                            throw new ArgumentException($"Malformed HWiNFO sensor key '{key}'.", nameof(key));
                        }

                        props["HwInfoRemoteIndex"] = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                        props["Id"] = uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        props["Instance"] = uint.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                        props["EntryId"] = uint.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    }
                case SensorType.Libre:
                    props["LibreSensorId"] = key.Id;
                    break;
                case SensorType.Plugin:
                    props["PluginSensorId"] = key.Id;
                    break;
            }

            return props;
        }

        /// <summary>
        /// Reads the item's sensor. A missing binding or reading gives null and never throws.
        /// </summary>
        internal static SensorReading? Read(DisplayItem item, ISensorReader sensors)
        {
            if (Of(item) is SensorKey key && sensors.TryRead(key, out var reading))
            {
                return reading;
            }

            return null;
        }
    }
}
