using InfoPanel.Monitors;
using InfoPanel.Plugins;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
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
    ///   libre.{slug}                                 LibreHardwareMonitor (identical devices: "{name} #2" → {slug}__2_...)
    ///   libre.{drive}_volumes / _drive_letters       a drive's volumes ("C: Windows") and letters
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

        // A plugin entry's key and ref never change, but the filtered streams
        // read every entry up to ten times a second per client.
        private static readonly ConcurrentDictionary<string, (string Key, string Ref)> PluginIds = new(StringComparer.Ordinal);

        private readonly SystemMetrics _system = new();
        private readonly object _lock = new();
        private DataApiSnapshot? _cached;
        private DateTime _cachedAt;
        private DataApiSnapshot? _streamJsonOf;
        private string? _streamJson;

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

        /// <summary>
        /// The current snapshot as an /events message body, serialized once
        /// per snapshot and shared by every full-stream client.
        /// </summary>
        public string GetStreamJson(string imageBaseUrl)
        {
            lock (_lock)
            {
                var snapshot = Get(imageBaseUrl);
                if (!ReferenceEquals(snapshot, _streamJsonOf))
                {
                    _streamJson = JsonSerializer.Serialize(snapshot, DataApiTask.StreamJsonOptions);
                    _streamJsonOf = snapshot;
                }
                return _streamJson!;
            }
        }

        /// <summary>
        /// The values whose key starts with <paramref name="prefix"/>. Plugin
        /// keys are read fresh (plugin hosts update every ~100 ms, faster than
        /// the shared snapshot); other keys come from the shared snapshot.
        /// </summary>
        public Dictionary<string, DataApiValue> GetMatching(string prefix, string imageBaseUrl)
        {
            var sensors = new Dictionary<string, DataApiValue>(StringComparer.OrdinalIgnoreCase);
            if (prefix.StartsWith("plugins.", StringComparison.OrdinalIgnoreCase))
            {
                AddPlugins(sensors, imageBaseUrl, prefix);
            }
            else
            {
                foreach (var (key, value) in Get(imageBaseUrl).Sensors)
                {
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) sensors[key] = value;
                }
            }
            return sensors;
        }

        /// <summary>Drops the cached snapshot (after a push), so the next read is current.</summary>
        public void Invalidate()
        {
            lock (_lock) _cached = null;
        }

        /* ---------------- plugins ---------------- */

        /// <summary>The Data API key of a PluginMonitor.SENSORHASH entry ("/{plugin}/{container}/{entry}").</summary>
        internal static string PluginKey(string hashKey) => "plugins." + hashKey.TrimStart('/').Replace('/', '.');

        /// <summary>The Ref of a plugin entry: its PluginSensorId as a profile stores it.</summary>
        internal static string PluginRef(string hashKey) => "plugin:" + hashKey;

        internal static bool IsImageContainer(string containerId) =>
            containerId.StartsWith(ImageContainerPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Adds the plugin entries, or with <paramref name="prefix"/> only those
        /// whose key starts with it: an entry that doesn't match is skipped
        /// before its value (a whole table, say) is built.
        /// </summary>
        private static void AddPlugins(Dictionary<string, DataApiValue> sensors, string imageBaseUrl, string? prefix = null)
        {
            foreach (var (hashKey, reading) in PluginMonitor.SENSORHASH)
            {
                try
                {
                    var (key, sensorRef) = PluginIds.GetOrAdd(hashKey, static h => (PluginKey(h), PluginRef(h)));
                    if (prefix != null && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
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
                                Ref = sensorRef,
                            };
                        }
                        continue;
                    }

                    sensors[key] = reading.Data switch
                    {
                        IPluginSensor sensor => FromPluginSensor(sensor, display),
                        IPluginText text => new DataApiValue { Text = text.Value, Name = display, Source = Source },
                        IPluginTable table => new DataApiValue { Text = FlattenTable(table.Value), Table = ToTable(table.Value, table.DefaultFormat), Name = display, Source = Source },
                        _ => new DataApiValue { Text = reading.Data?.ToString(), Name = display, Source = Source },
                    } with { Ref = sensorRef };
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

        /// <summary>
        /// A plugin table as a table sensor item draws it: the column names,
        /// and each cell's text, which PanelDraw takes from IPluginData.ToString()
        /// and leaves blank for anything else (null here), with the plugin's
        /// default column format.
        /// </summary>
        internal static DataApiTable? ToTable(DataTable? table, string? format = null)
        {
            if (table == null) return null;

            var columns = new List<string>(table.Columns.Count);
            foreach (DataColumn column in table.Columns) columns.Add(column.ColumnName);

            var rows = new List<IReadOnlyList<string?>>(table.Rows.Count);
            foreach (DataRow row in table.Rows)
            {
                var cells = new string?[columns.Count];
                for (int i = 0; i < cells.Length; i++)
                {
                    cells[i] = row[i] is IPluginData data ? data.ToString() : null;
                }
                rows.Add(cells);
            }
            return new DataApiTable { Columns = columns, Rows = rows, Format = string.IsNullOrEmpty(format) ? null : format };
        }

        /* ---------------- LibreHardwareMonitor ---------------- */

        private static void AddLibre(Dictionary<string, DataApiValue> sensors)
        {
            foreach (var (key, name, sensor, hashKey) in LibreKeys())
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
                        Ref = LibreRef(hashKey),
                    };
                }
                catch
                {
                    // Skip malformed entries
                }
            }
            AddLibreVolumes(sensors);
        }

        /// <summary>
        /// The volumes on each physical drive, so a drive can be picked by its
        /// letter and label instead of its model name:
        ///   libre.{drive}_volumes        "C: Windows, D: Games" (label when set)
        ///   libre.{drive}_drive_letters  "C:, D:"
        /// {drive} is the same device slug its sensors use ("kingston_sfyrd2000g__2").
        /// </summary>
        private static void AddLibreVolumes(Dictionary<string, DataApiValue> sensors)
        {
            var hardwareNames = LibreHardwareNames(LibreMonitor.SENSORHASH.Values);
            var storages = LibreMonitor.SENSORHASH.Values
                .Select(s => s.Hardware)
                .OfType<StorageDevice>()
                .DistinctBy(h => h.Identifier.ToString());
            foreach (var storage in storages)
            {
                try
                {
                    if (!hardwareNames.TryGetValue(storage.Identifier.ToString(), out var device)) continue;
                    var volumes = storage.Storage.Partitions
                        .Select(p => VolumeOf(p.DriveLetter?.ToString()))
                        .Where(v => v.Letter.Length > 0)
                        .DistinctBy(v => v.Letter)
                        .OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (volumes.Count == 0) continue;
                    string slug = Slug(device);
                    sensors[$"libre.{slug}_volumes"] = new DataApiValue
                    {
                        Text = string.Join(", ", volumes.Select(v => v.Label.Length > 0 ? $"{v.Letter} {v.Label}" : v.Letter)),
                        Name = $"{device} Volumes",
                        Source = Source,
                    };
                    sensors[$"libre.{slug}_drive_letters"] = new DataApiValue
                    {
                        Text = string.Join(", ", volumes.Select(v => v.Letter)),
                        Name = $"{device} Drive Letters",
                        Source = Source,
                    };
                }
                catch
                {
                    // A volume can go away mid-read; skip the drive this cycle.
                }
            }
        }

        /// <summary>
        /// A partition's letter ("C:") and volume label (empty when unset or
        /// unreadable); no letter for a partition without one.
        /// </summary>
        private static (string Letter, string Label) VolumeOf(string? driveLetter)
        {
            char c = (driveLetter ?? "").Trim().FirstOrDefault();
            if (!char.IsLetter(c)) return ("", "");
            string letter = $"{char.ToUpperInvariant(c)}:";
            try
            {
                var drive = new System.IO.DriveInfo(letter);
                return (letter, drive.IsReady ? CleanName(drive.VolumeLabel) : "");
            }
            catch
            {
                return (letter, "");
            }
        }

        /// <summary>The display name of every device that has sensors, by identifier (see HardwareNames).</summary>
        private static Dictionary<string, string> LibreHardwareNames(IEnumerable<ISensor> sensors)
        {
            var devices = new List<(string Identifier, string Name)>();
            foreach (var sensor in sensors)
            {
                try
                {
                    devices.Add((sensor.Hardware.Identifier.ToString(), sensor.Hardware.Name ?? ""));
                }
                catch
                {
                    // Skip malformed entries
                }
            }
            return HardwareNames(devices);
        }

        /// <summary>
        /// Assigns every LibreHardwareMonitor sensor a unique, stable key.
        /// "{hardware} {sensor}" is not unique (e.g. "GPU Core" is a
        /// temperature, a clock and a load): the highest-precedence type keeps
        /// "libre.{slug}", the others get "_{sensortype}", plus "_{index}" if
        /// that still collides.
        /// </summary>
        internal static List<(string Key, string Name, ISensor Sensor, string HashKey)> LibreKeys()
        {
            var entries = LibreMonitor.SENSORHASH.ToList();
            var hardwareNames = LibreHardwareNames(entries.Select(e => e.Value));

            var named = new List<(string Slug, string Name, ISensor Sensor, string HashKey)>();
            foreach (var (hashKey, sensor) in entries)
            {
                try
                {
                    string hardware = hardwareNames.TryGetValue(sensor.Hardware.Identifier.ToString(), out var n) ? n : CleanName(sensor.Hardware.Name);
                    string name = CleanName($"{hardware} {sensor.Name}".Trim());
                    string slug = Slug(name);
                    if (slug.Length > 0) named.Add((slug, name, sensor, hashKey));
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
                (key, n) => ($"libre.{key}", n.Name, n.Sensor, n.HashKey));
        }

        /// <summary>
        /// The Ref of a LibreHardwareMonitor sensor: its LibreMonitor.SENSORHASH
        /// key. That is the identifier a profile's LibreSensorId stores; of two
        /// sensors sharing an identifier, the one InfoPanel reads for it keeps
        /// the identifier and the other has "{identifier}/{name}", which no
        /// profile binds to.
        /// </summary>
        internal static string LibreRef(string hashKey) => "libre:" + hashKey;

        /// <summary>
        /// The display name of each LibreHardwareMonitor device, by identifier.
        /// Identical devices (three of the same NVMe model) share a name, so
        /// their sensors used to collide and get arbitrary suffixes: the first
        /// by identifier keeps the plain name (existing keys stay put), the
        /// others become "{name} #2", "#3", ... in identifier order
        /// ("/nvme/2" before "/nvme/10"), which is stable across restarts.
        /// </summary>
        internal static Dictionary<string, string> HardwareNames(IEnumerable<(string Identifier, string Name)> hardware)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in hardware.DistinctBy(h => h.Identifier).GroupBy(h => CleanName(h.Name), StringComparer.OrdinalIgnoreCase))
            {
                int i = 0;
                foreach (var (identifier, _) in group.OrderBy(h => h.Identifier, IdentifierOrder.Instance))
                {
                    names[identifier] = i == 0 ? group.Key : $"{group.Key} #{i + 1}";
                    i++;
                }
            }
            return names;
        }

        /// <summary>Orders identifiers with their numeric segments compared as numbers.</summary>
        private sealed class IdentifierOrder : IComparer<string>
        {
            public static readonly IdentifierOrder Instance = new();

            public int Compare(string? x, string? y)
            {
                string[] a = (x ?? "").Split('/'), b = (y ?? "").Split('/');
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                {
                    int c = int.TryParse(a[i], out int na) && int.TryParse(b[i], out int nb)
                        ? na.CompareTo(nb)
                        : string.CompareOrdinal(a[i], b[i]);
                    if (c != 0) return c;
                }
                return a.Length.CompareTo(b.Length);
            }
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
                    Ref = HwInfoRef(reading),
                };
            }
        }

        /// <summary>
        /// The Ref of a HWiNFO reading: its HWHash.SENSORHASH key, which a
        /// profile stores as HwInfoRemoteIndex, Id, Instance and EntryId.
        /// </summary>
        internal static string HwInfoRef(HWHash.HWINFO_HASH reading) =>
            $"hwinfo:{reading.RemoteIndex}/{reading.ParentID}/{reading.ParentInstance}/{reading.SensorID}";

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

        /// <summary>
        /// The HWiNFO devices (sensors) with their readings, for /api/sources:
        /// one device per remote, sensor id and instance, in HWiNFO's order,
        /// each entry carrying the key and ref it has in /api/sensors.
        /// </summary>
        internal static List<HwInfoGroup> HwInfoTree() => HwInfoTree(HwInfoKeys());

        internal static List<HwInfoGroup> HwInfoTree(IEnumerable<(string Key, string Name, HWHash.HWINFO_HASH Reading)> keyed) =>
            keyed
                .GroupBy(k => (k.Reading.RemoteIndex, k.Reading.ParentID, k.Reading.ParentInstance))
                .OrderBy(g => g.Min(k => k.Reading.IndexOrder))
                .Select(g =>
                {
                    var first = g.First().Reading;
                    string device = HwInfoDevice(first);
                    return new HwInfoGroup
                    {
                        Id = $"{first.RemoteIndex}/{first.ParentID}/{first.ParentInstance}",
                        Name = device.Length > 0 ? device : $"Sensor {first.ParentID}",
                        SensorId = first.ParentID,
                        Instance = first.ParentInstance,
                        Remote = first.RemoteIndex,
                        Entries = g
                            .OrderBy(k => k.Reading.IndexOrder)
                            .Select(k => new HwInfoReading
                            {
                                Id = k.Reading.SensorID.ToString(),
                                Name = k.Name,
                                Key = k.Key,
                                Ref = HwInfoRef(k.Reading),
                            })
                            .ToList(),
                    };
                })
                .ToList();

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
