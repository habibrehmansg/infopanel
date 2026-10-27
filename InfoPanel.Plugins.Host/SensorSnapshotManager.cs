using System.Collections.Concurrent;
using System.Data;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;

namespace InfoPanel.Plugins.Host
{
    /// <summary>
    /// Finds the plugin entries whose values changed since the host last delivered them. <see cref="GetChangedEntries"/>
    /// only reads: what it returns counts as sent once the host confirms delivery with <see cref="MarkSent"/>, so a
    /// cycle that fails part way, or whose message is never sent, costs no entry its update.
    /// </summary>
    public class SensorSnapshotManager
    {
        // Per entry key: what the host last delivered.
        private readonly ConcurrentDictionary<string, object?> _lastValues = new();
        // Per entry key: what GetChangedEntries last returned as a change, until MarkSent confirms it delivered.
        private readonly ConcurrentDictionary<string, object?> _pending = new();
        private readonly ConcurrentDictionary<(string containerId, string entryId), string> _keyCache = new();

        public List<EntryUpdateDto> GetChangedEntries(PluginWrapper wrapper)
        {
            var updates = new List<EntryUpdateDto>();

            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var key = Key(wrapper.Id, container.Id, entry.Id);
                    EntryUpdateDto? update;
                    try
                    {
                        update = CreateUpdateIfChanged(key, container.Id, entry);
                    }
                    catch (Exception ex) when (entry is IPluginTable && IsTableChangedDuringRead(ex))
                    {
                        // The plugin modified a table while it was being read. Skip only this entry:
                        // nothing is recorded for it, so it is re-checked next cycle.
                        // Other entry types still surface their errors to the update loop.
                        continue;
                    }

                    if (update != null)
                    {
                        updates.Add(update);
                    }
                }
            }

            return updates;
        }

        /// <summary>
        /// Records <paramref name="updates"/>, returned by <see cref="GetChangedEntries"/> for the plugin
        /// <paramref name="pluginId"/>, as delivered: each is compared with later reads and is not sent again until it
        /// changes. Call it only once the host has sent them.
        /// </summary>
        public void MarkSent(string pluginId, IEnumerable<EntryUpdateDto> updates)
        {
            foreach (var update in updates)
            {
                var key = Key(pluginId, update.ContainerId, update.EntryId);
                if (_pending.TryRemove(key, out var value))
                {
                    _lastValues[key] = value;
                }
            }
        }

        private string Key(string pluginId, string containerId, string entryId) =>
            _keyCache.GetOrAdd((containerId, entryId), k => $"{pluginId}/{k.containerId}/{k.entryId}");

        // A DataTable is not thread-safe: a plugin that changes rows or columns while the host reads them makes the
        // read throw. "Collection was modified" (InvalidOperationException), a removed row (RowNotInTableException, a
        // DataException), a column removed between finding it and reading its storage (NullReferenceException) or an
        // index past a collection that shrank (IndexOutOfRangeException, ArgumentOutOfRangeException).
        private static bool IsTableChangedDuringRead(Exception ex) =>
            ex is InvalidOperationException or DataException or NullReferenceException
                or IndexOutOfRangeException or ArgumentOutOfRangeException;

        private EntryUpdateDto? CreateUpdateIfChanged(string key, string containerId, IPluginData entry)
        {
            if (entry is IPluginSensor sensor)
            {
                var currentHash = HashCode.Combine(sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg);
                if (_lastValues.TryGetValue(key, out var lastHash) && lastHash is int lastInt && lastInt == currentHash)
                    return null;

                _pending[key] = currentHash;
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "sensor",
                    SensorValue = new SensorValueDto
                    {
                        Value = sensor.Value,
                        ValueMin = sensor.ValueMin,
                        ValueMax = sensor.ValueMax,
                        ValueAvg = sensor.ValueAvg,
                        Unit = sensor.Unit
                    }
                };
            }
            else if (entry is IPluginText text)
            {
                if (_lastValues.TryGetValue(key, out var lastVal) && lastVal is string lastStr && lastStr == text.Value)
                    return null;

                _pending[key] = text.Value;
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "text",
                    TextValue = text.Value
                };
            }
            else if (entry is IPluginTable table)
            {
                var hash = ComputeTableHash(table);
                if (_lastValues.TryGetValue(key, out var lastHash) && lastHash is int lastInt && lastInt == hash)
                    return null;

                // Convert before recording the hash: if the conversion throws,
                // the change must still be sent on the next cycle.
                var tableValue = TableDtoConverter.ConvertTableToDto(table);
                _pending[key] = hash;
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "table",
                    TableValue = tableValue
                };
            }

            return null;
        }

        private static int ComputeTableHash(IPluginTable table)
        {
            var dt = table.Value;
            if (dt == null) return 0;

            var hash = HashCode.Combine(dt.Rows.Count, dt.Columns.Count);

            foreach (DataRow row in dt.Rows)
            {
                for (int i = 0; i < dt.Columns.Count; i++)
                {
                    var value = row[i];
                    if (value is IPluginSensor sensor)
                    {
                        hash = HashCode.Combine(hash, sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg);
                    }
                    else if (value is IPluginText text)
                    {
                        hash = HashCode.Combine(hash, text.Value?.GetHashCode() ?? 0);
                    }
                    else
                    {
                        hash = HashCode.Combine(hash, value?.GetHashCode() ?? 0);
                    }
                }
            }

            return hash;
        }

        public void Clear()
        {
            _lastValues.Clear();
            _pending.Clear();
            _keyCache.Clear();
        }
    }
}
