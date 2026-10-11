using InfoPanel.Monitors;
using InfoPanel.Plugins;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;

namespace InfoPanel.DataApi
{
    /// <summary>
    /// Builds the Data API snapshot straight from the app's live sensor
    /// data — every plugin entry, LibreHardwareMonitor, HWiNFO, system load
    /// and external pushes — so nothing has to be copied or pruned: a
    /// stopped plugin's keys are simply absent from the next snapshot.
    ///
    /// Keys:
    ///   plugins.{pluginId}.{containerId}.{entryId}   plugin entries
    ///   libre.{slug}                                 LibreHardwareMonitor
    ///   hwinfo.{slug} / hwinfo.r{n}_{slug}           HWiNFO (local / remote)
    ///   system.*                                     CPU / RAM load
    /// </summary>
    internal sealed partial class SensorSnapshotBuilder(PushStore pushStore)
    {
        // Snapshots are shared by every REST and SSE client: build at most
        // one per interval.
        private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(900);

        private const string Source = "infopanel";
        private const string ImageContainerPrefix = "__images_";
        private const string PluginImageScheme = "plugin-image://";

        // Which reading keeps the bare "libre.{slug}" key when several sensor
        // types share a name. Temperature first: that is what existing
        // dashboard layouts bind the bare GPU/CPU keys to.
        private static readonly SensorType[] LibreKeyPrecedence =
        [
            SensorType.Temperature, SensorType.Load, SensorType.Power, SensorType.Clock,
            SensorType.Fan, SensorType.Control, SensorType.Voltage, SensorType.Current,
            SensorType.Throughput, SensorType.Data, SensorType.SmallData,
        ];

        private readonly SystemMetrics _system = new();
        private readonly object _lock = new();
        private DataApiSnapshot? _cached;
        private DateTime _cachedAt;

        /// <summary>
        /// The current snapshot. <paramref name="imageBaseUrl"/> is the API's
        /// own base URL; plugin images are linked as {base}/api/images/....
        /// </summary>
        public DataApiSnapshot Get(string imageBaseUrl)
        {
            lock (_lock)
            {
                if (_cached != null && DateTime.UtcNow - _cachedAt < MaxAge) return _cached;

                var sensors = new Dictionary<string, DataApiValue>(StringComparer.OrdinalIgnoreCase);
                // External pushes first: InfoPanel's own values win on a key clash.
                pushStore.AddTo(sensors);
                _system.AddTo(sensors);
                AddHwInfo(sensors);
                AddLibre(sensors);
                AddPlugins(sensors, imageBaseUrl);

                _cached = new DataApiSnapshot
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Sensors = sensors,
                    Sources = pushStore.GetSources(),
                };
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }

        /// <summary>Drops the cached snapshot (after a push), so the next read is current.</summary>
        public void Invalidate()
        {
            lock (_lock) _cached = null;
        }

        /* ---------------- plugins ---------------- */

        /// <summary>The Data API key of a PluginMonitor.SENSORHASH entry ("/{plugin}/{container}/{entry}").</summary>
        internal static string PluginKey(string hashKey) => "plugins." + hashKey.TrimStart('/').Replace('/', '.');

        internal static bool IsImageContainer(string containerId) =>
            containerId.StartsWith(ImageContainerPrefix, StringComparison.Ordinal);

        private static void AddPlugins(Dictionary<string, DataApiValue> sensors, string imageBaseUrl)
        {
            foreach (var (hashKey, reading) in PluginMonitor.SENSORHASH)
            {
                try
                {
                    string key = PluginKey(hashKey);
                    string display = DisplayName(reading.ContainerName, reading.Name);

                    if (reading.ContainerId.StartsWith(ImageContainerPrefix, StringComparison.Ordinal))
                    {
                        // Plugin images are exposed as "plugin-image://{plugin}/{image}"
                        // text entries; link them to /api/images instead.
                        if (reading.Data is IPluginText { Value: var link } && link.StartsWith(PluginImageScheme, StringComparison.Ordinal))
                        {
                            sensors[key] = new DataApiValue
                            {
                                Text = $"{imageBaseUrl}/api/images/{link[PluginImageScheme.Length..]}",
                                Name = display,
                                Source = Source,
                            };
                        }
                        continue;
                    }

                    sensors[key] = reading.Data switch
                    {
                        IPluginSensor sensor => FromPluginSensor(sensor, display),
                        IPluginText text => new DataApiValue { Text = text.Value, Name = display, Source = Source },
                        IPluginTable table => new DataApiValue { Text = FlattenTable(table.Value), Name = display, Source = Source },
                        _ => new DataApiValue { Text = reading.Data?.ToString(), Name = display, Source = Source },
                    };
                }
                catch
                {
                    // A proxy can be swapped mid-read; skip it this cycle.
                }
            }
        }

        private static DataApiValue FromPluginSensor(IPluginSensor sensor, string display)
        {
            // All-zero min/max/avg next to a non-zero value is the interface
            // default of a plugin that does not track a range: leave it out.
            bool hasRange = sensor.ValueMin != 0 || sensor.ValueMax != 0 || sensor.ValueAvg != 0 || sensor.Value == 0;
            return new DataApiValue
            {
                Value = sensor.Value,
                Unit = sensor.Unit ?? "",
                Min = hasRange ? sensor.ValueMin : null,
                Max = hasRange ? sensor.ValueMax : null,
                Avg = hasRange ? sensor.ValueAvg : null,
                Name = display,
                Source = Source,
            };
        }

        /// <summary>
        /// Flattens a plugin table to multi-line text: one line per row, cells
        /// joined with ", ".
        /// </summary>
        internal static string FlattenTable(DataTable? table)
        {
            if (table == null || table.Rows.Count == 0) return "-";

            var lines = new List<string>(table.Rows.Count);
            foreach (DataRow row in table.Rows)
            {
                var cells = new string[table.Columns.Count];
                for (int i = 0; i < cells.Length; i++)
                {
                    cells[i] = row[i] switch
                    {
                        IPluginText text => text.Value,
                        IPluginSensor sensor => sensor.ToString() ?? "",
                        var other => other?.ToString() ?? ""
                    };
                }
                lines.Add(string.Join(", ", cells));
            }
            return string.Join('\n', lines);
        }

        /* ---------------- LibreHardwareMonitor ---------------- */

        private static void AddLibre(Dictionary<string, DataApiValue> sensors)
        {
            foreach (var (key, name, sensor) in LibreKeys())
            {
                try
                {
                    sensors[key] = new DataApiValue
                    {
                        Value = sensor.Value,
                        Min = sensor.Min,
                        Max = sensor.Max,
                        Unit = sensor.GetUnit() ?? "",
                        Name = name,
                        Source = Source,
                    };
                }
                catch
                {
                    // Skip malformed entries
                }
            }
        }

        /// <summary>
        /// Assigns every LibreHardwareMonitor sensor a unique, stable key.
        /// "{hardware} {sensor}" is not unique (e.g. "GPU Core" is a
        /// temperature, a clock and a load): the highest-precedence type keeps
        /// "libre.{slug}", the others get "_{sensortype}", plus "_{index}" if
        /// that still collides.
        /// </summary>
        internal static List<(string Key, string Name, ISensor Sensor)> LibreKeys()
        {
            var named = new List<(string Slug, string Name, ISensor Sensor)>();
            foreach (var sensor in LibreMonitor.SENSORHASH.Values)
            {
                try
                {
                    string name = CleanName($"{sensor.Hardware.Name} {sensor.Name}".Trim());
                    string slug = Slug(name);
                    if (slug.Length > 0) named.Add((slug, name, sensor));
                }
                catch
                {
                    // Skip malformed entries
                }
            }

            static int Rank(SensorType type)
            {
                int i = Array.IndexOf(LibreKeyPrecedence, type);
                return i < 0 ? LibreKeyPrecedence.Length : i;
            }

            return Disambiguate(
                named,
                n => n.Slug,
                group => group
                    .OrderBy(n => Rank(n.Sensor.SensorType))
                    .ThenBy(n => n.Sensor.Identifier.ToString(), StringComparer.Ordinal),
                [
                    n => n.Sensor.SensorType.ToString().ToLowerInvariant(),
                    n => n.Sensor.Index.ToString(),
                ],
                (key, n) => ($"libre.{key}", n.Name, n.Sensor));
        }

        /* ---------------- HWiNFO ---------------- */

        private static void AddHwInfo(Dictionary<string, DataApiValue> sensors)
        {
            foreach (var (key, name, reading) in HwInfoKeys())
            {
                sensors[key] = new DataApiValue
                {
                    Value = reading.ValueNow,
                    Unit = reading.Unit ?? "",
                    Min = reading.ValueMin,
                    Max = reading.ValueMax,
                    Avg = reading.ValueAvg,
                    Name = reading.RemoteIndex >= 0 ? $"Remote {reading.RemoteIndex}: {name}" : name,
                    Source = Source,
                };
            }
        }

        /// <summary>The HWiNFO device (parent) name of a reading.</summary>
        internal static string HwInfoDevice(HWHash.HWINFO_HASH reading) =>
            CleanName(!string.IsNullOrWhiteSpace(reading.ParentNameCustom) ? reading.ParentNameCustom : reading.ParentNameDefault);

        /// <summary>
        /// Assigns every HWiNFO reading a unique, stable key. Names repeat
        /// across devices ("Read Activity" per drive): the first reading keeps
        /// "hwinfo.{slug}", the others get their device name appended, then
        /// the device instance.
        /// </summary>
        internal static List<(string Key, string Name, HWHash.HWINFO_HASH Reading)> HwInfoKeys()
        {
            var named = new List<(string Slug, string Name, HWHash.HWINFO_HASH Reading)>();
            foreach (var reading in HWHash.SENSORHASH.Values)
            {
                string name = CleanName(!string.IsNullOrWhiteSpace(reading.NameCustom) ? reading.NameCustom : reading.NameDefault);
                string slug = Slug(name);
                if (slug.Length == 0) continue;
                string prefix = reading.RemoteIndex >= 0 ? $"r{reading.RemoteIndex}_" : "";
                named.Add((prefix + slug, name, reading));
            }

            return Disambiguate(
                named,
                n => n.Slug,
                group => group.OrderBy(n => n.Reading.IndexOrder),
                [
                    n => Slug(HwInfoDevice(n.Reading)),
                    n => n.Reading.ParentInstance.ToString(),
                ],
                (key, n) => ($"hwinfo.{key}", n.Name, n.Reading));
        }

        /* ---------------- shared helpers ---------------- */

        /// <summary>
        /// Gives every item a unique key. Items are grouped by base key; the
        /// first item of each ordered group keeps the base key, the rest get
        /// the qualifiers appended one at a time until the key is unused.
        /// </summary>
        private static List<TResult> Disambiguate<T, TResult>(
            List<T> items,
            Func<T, string> baseKey,
            Func<IEnumerable<T>, IEnumerable<T>> order,
            Func<T, string>[] qualifiers,
            Func<string, T, TResult> result)
        {
            var output = new List<TResult>(items.Count);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in items.GroupBy(baseKey))
            {
                bool first = true;
                foreach (var item in order(group))
                {
                    string key = group.Key;
                    if (!first)
                    {
                        foreach (var qualifier in qualifiers)
                        {
                            string q = qualifier(item);
                            if (q.Length > 0) key = $"{key}_{q}";
                            if (!used.Contains(key)) break;
                        }
                    }
                    first = false;
                    used.Add(key);
                    output.Add(result(key, item));
                }
            }
            return output;
        }

        private static string DisplayName(string containerName, string entryName)
        {
            string display = CleanName(entryName);
            string container = CleanName(containerName);
            return !string.IsNullOrWhiteSpace(container) && container != display
                ? $"{container} · {display}"
                : display;
        }

        /// <summary>
        /// Removes control characters (null terminators, ...) and collapses
        /// whitespace. Some LibreHardwareMonitor and HWiNFO sensor names carry
        /// raw null bytes from the underlying shared memory.
        /// </summary>
        internal static string CleanName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string cleaned = new(value.Where(c => c >= 0x20).ToArray());
            return Whitespace().Replace(cleaned, " ").Trim();
        }

        /// <summary>Turns a display name into a key slug: lowercase, every non letter/digit becomes '_'.</summary>
        internal static string Slug(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var chars = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                chars[i] = char.IsLetterOrDigit(c) ? c : '_';
            }
            return new string(chars).Trim('_');
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();
    }
}
