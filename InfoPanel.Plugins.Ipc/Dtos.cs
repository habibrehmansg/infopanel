using System.Data;

namespace InfoPanel.Plugins.Ipc
{
    public class PluginMetadataDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string? ConfigFilePath { get; set; }
        public double UpdateIntervalMs { get; set; }
        public List<PluginActionDto> Actions { get; set; } = [];
        public bool IsConfigurable { get; set; }
        public List<PluginConfigPropertyDto> ConfigProperties { get; set; } = [];
    }

    public class PluginActionDto
    {
        public string MethodName { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public class ContainerDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsEphemeralPath { get; set; }
        public List<EntryDto> Entries { get; set; } = [];
    }

    public class EntryDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>
        /// "sensor", "text", or "table"
        /// </summary>
        public string Type { get; set; } = "";
        public SensorValueDto? SensorValue { get; set; }
        public string? TextValue { get; set; }
        public TableValueDto? TableValue { get; set; }
    }

    public class SensorValueDto
    {
        public float Value { get; set; }
        public float ValueMin { get; set; }
        public float ValueMax { get; set; }
        public float ValueAvg { get; set; }
        public string? Unit { get; set; }
    }

    public class TableValueDto
    {
        public List<string> Columns { get; set; } = [];
        public List<List<TableCellDto>> Rows { get; set; } = [];
        public string DefaultFormat { get; set; } = "";
    }

    public class TableCellDto
    {
        /// <summary>
        /// "sensor" or "text"
        /// </summary>
        public string Type { get; set; } = "";
        public string? TextValue { get; set; }
        public SensorValueDto? SensorValue { get; set; }
        public string? SensorName { get; set; }
        public string? SensorUnit { get; set; }
    }

    public class SensorUpdateBatchDto
    {
        public string PluginId { get; set; } = "";
        public List<EntryUpdateDto> Updates { get; set; } = [];
    }

    public class EntryUpdateDto
    {
        public string ContainerId { get; set; } = "";
        public string EntryId { get; set; } = "";
        /// <summary>
        /// "sensor", "text", or "table"
        /// </summary>
        public string Type { get; set; } = "";
        public SensorValueDto? SensorValue { get; set; }
        public string? TextValue { get; set; }
        public TableValueDto? TableValue { get; set; }
    }

    public class PluginPerformanceDto
    {
        public string PluginId { get; set; } = "";
        public long UpdateTimeMilliseconds { get; set; }
    }

    public class PluginConfigPropertyDto
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Description { get; set; }
        public string Type { get; set; } = ""; // "String", "Integer", "Double", "Boolean", "Choice"
        public object? Value { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? Step { get; set; }
        public string[]? Options { get; set; }
    }
}
