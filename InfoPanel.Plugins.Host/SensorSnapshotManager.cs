using System.Collections.Concurrent;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;

namespace InfoPanel.Plugins.Host
{
    public class SensorSnapshotManager
    {
        private readonly ConcurrentDictionary<string, object?> _lastValues = new();

        public List<EntryUpdateDto> GetChangedEntries(PluginWrapper wrapper)
        {
            var updates = new List<EntryUpdateDto>();

            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var key = $"{wrapper.Id}/{container.Id}/{entry.Id}";
                    var update = CreateUpdateIfChanged(key, container.Id, entry);
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
                // Always push table updates (change detection for DataTable is complex)
                return new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "table",
                    TableValue = TableDtoConverter.ConvertTableToDto(table)
                };
            }

            return null;
        }

        public void Clear()
        {
            _lastValues.Clear();
        }
    }
}
