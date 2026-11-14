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
        public List<ImageDescriptorDto> ImageDescriptors { get; set; } = [];
    }

    public class ImageDescriptorDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string MmfName { get; set; } = "";
        public long BufferSize { get; set; }
    }

    public class PluginActionDto
    {
        public string MethodName { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    /// <summary>An action's state, as the plugin describes it (<c>IPluginActionStates</c>), and whether it runs now.</summary>
    public class ActionStateDto
    {
        public bool Enabled { get; set; } = true;
        public string? State { get; set; }
        public ActionConfirmDto? Confirm { get; set; }
        public bool Running { get; set; }
    }

    /// <summary>What a person confirms before an action runs: a title and plain text.</summary>
    public class ActionConfirmDto
    {
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
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
        /// "sensor", "text", "table" or "image"; an image is listed without its picture, which an update sends
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
        /// "sensor", "text", "table" or "image"
        /// </summary>
        public string Type { get; set; } = "";
        public SensorValueDto? SensorValue { get; set; }
        public string? TextValue { get; set; }
        public TableValueDto? TableValue { get; set; }
        /// <summary>
        /// An image's picture, its encoded bytes as the plugin gave them, as base64 text (<see cref="PictureText"/>), at
        /// most <see cref="IpcLimits.ImageBytes"/>; null when it holds none. Text, not bytes, so that reading a message
        /// decodes no picture: the engine decodes one only once it has admitted it, its size known from the text's length
        /// first. An update with a picture is the only update of its message: a picture of 8 MiB is about 10.7 MiB as
        /// base64, within the engine's 16 MiB but over the host's own share for a message of updates.
        /// </summary>
        public string? ImageValue { get; set; }
    }

    public class PluginPerformanceDto
    {
        public string PluginId { get; set; } = "";
        public long UpdateTimeMilliseconds { get; set; }
        public double CpuPercent { get; set; }
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
