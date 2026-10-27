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

        // Each entry is read once per cycle. The change check compares what was read with what was last delivered,
        // and the payload is built from the same read, so the check always describes the payload sent.
        private EntryUpdateDto? CreateUpdateIfChanged(string key, string containerId, IPluginData entry)
        {
            object state;
            EntryUpdateDto update;

            if (entry is IPluginSensor sensor)
            {
                var reading = new SensorReading(sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg);
                var unit = sensor.Unit;
                state = reading;
                update = new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "sensor",
                    SensorValue = new SensorValueDto
                    {
                        Value = reading.Value,
                        ValueMin = reading.ValueMin,
                        ValueMax = reading.ValueMax,
                        ValueAvg = reading.ValueAvg,
                        Unit = unit
                    }
                };
            }
            else if (entry is IPluginText text)
            {
                var value = text.Value;
                state = value;
                update = new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "text",
                    TextValue = value
                };
            }
            else if (entry is IPluginTable table)
            {
                // The payload is the table's only read; it is compared whole, never through a hash of a second read.
                var tableValue = TableDtoConverter.ConvertTableToDto(table);
                state = tableValue;
                update = new EntryUpdateDto
                {
                    ContainerId = containerId,
                    EntryId = entry.Id,
                    Type = "table",
                    TableValue = tableValue
                };
            }
            else
            {
                return null;
            }

            if (_lastValues.TryGetValue(key, out var last) && SameState(last, state))
                return null;

            _pending[key] = state;
            return update;
        }

        private readonly record struct SensorReading(float Value, float ValueMin, float ValueMax, float ValueAvg);

        private static bool SameState(object? last, object state) => (last, state) switch
        {
            (SensorReading a, SensorReading b) => a == b,
            (string a, string b) => a == b,
            (TableValueDto a, TableValueDto b) => SameTable(a, b),
            _ => false
        };

        private static bool SameTable(TableValueDto a, TableValueDto b)
        {
            if (a.DefaultFormat != b.DefaultFormat
                || !a.Columns.SequenceEqual(b.Columns)
                || a.Rows.Count != b.Rows.Count)
                return false;

            for (var r = 0; r < a.Rows.Count; r++)
            {
                var rowA = a.Rows[r];
                var rowB = b.Rows[r];
                if (rowA.Count != rowB.Count) return false;

                for (var c = 0; c < rowA.Count; c++)
                {
                    if (!SameCell(rowA[c], rowB[c])) return false;
                }
            }

            return true;
        }

        private static bool SameCell(TableCellDto a, TableCellDto b) =>
            a.Type == b.Type
            && a.TextValue == b.TextValue
            && a.SensorName == b.SensorName
            && a.SensorUnit == b.SensorUnit
            && SameSensorValue(a.SensorValue, b.SensorValue);

        private static bool SameSensorValue(SensorValueDto? a, SensorValueDto? b) =>
            a == null || b == null
                ? a == b
                : a.Value.Equals(b.Value)
                  && a.ValueMin.Equals(b.ValueMin)
                  && a.ValueMax.Equals(b.ValueMax)
                  && a.ValueAvg.Equals(b.ValueAvg)
                  && a.Unit == b.Unit;

        public void Clear()
        {
            _lastValues.Clear();
            _pending.Clear();
            _keyCache.Clear();
        }
    }
}
