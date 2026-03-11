namespace InfoPanel.Plugins
{
    public enum PluginConfigType
    {
        String,
        Integer,
        Double,
        Boolean,
        Choice
    }

    public class PluginConfigProperty
    {
        public required string Key { get; init; }
        public required string DisplayName { get; init; }
        public string? Description { get; init; }
        public required PluginConfigType Type { get; init; }
        public object? Value { get; set; }
        public double? MinValue { get; init; }
        public double? MaxValue { get; init; }
        public double? Step { get; init; }
        public string[]? Options { get; init; }
    }

    public interface IPluginConfigurable
    {
        IReadOnlyList<PluginConfigProperty> ConfigProperties { get; }
        void ApplyConfig(string key, object? value);
    }
}
