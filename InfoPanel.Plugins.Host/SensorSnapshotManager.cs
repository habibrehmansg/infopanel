using System.Collections.Concurrent;
using System.Data;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;

namespace InfoPanel.Plugins.Host
{
    public class SensorSnapshotManager
    {
        private readonly ConcurrentDictionary<string, object?> _lastValues = new();
        private readonly ConcurrentDictionary<(string containerId, string entryId), string> _keyCache = new();

        public List<EntryUpdateDto> GetChangedEntries(PluginWrapper wrapper)
        {
            var updates = new List<EntryUpdateDto>();

            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var key = _keyCache.GetOrAdd(
                        (container.Id, entry.Id),
                        k => $"{wrapper.Id}/{k.containerId}/{k.entryId}");
                    EntryUpdateDto? update;
                    try
                    {
                        update = CreateUpdateIfChanged(key, container.Id, entry);
                    }
                    catch (Exception ex) when (entry is IPluginTable && ex is InvalidOperationException or DataException)
                    {
                        // The plugin modified a table while it was being read
                        // ("Collection was modified", or a row removed mid-read:
                        // RowNotInTableException). Skip only this entry: its
                        // last value is not recorded, so it is re-checked next cycle.
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

        private EntryUpdateDto? CreateUpdateIfChanged(string key, string containerId, IPluginData entry)
        {
            if (entry is IPluginSensor sensor)
            {
                var currentHash = HashCode.Combine(sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg);
                if (_lastValues.TryGetValue(key, out var lastHash) && lastHash is int lastInt && lastInt == currentHash)
                    return null;

                _lastValues[key] = currentHash;
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

                _lastValues[key] = text.Value;
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
                _lastValues[key] = hash;
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
            _keyCache.Clear();
        }
    }
}
