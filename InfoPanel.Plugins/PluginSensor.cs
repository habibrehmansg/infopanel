namespace InfoPanel.Plugins
{
    public class PluginSensor : IPluginSensor
    {
        public string Id { get; }

        public string Name { get; }

        public float Value { get; set; }

        public string? Unit { get; }

        public PluginSensor(string id, string name, float value, string? unit = null)
        {
            Id = id;
            Name = name;
            Value = value;
            Unit = unit;
        }

        public PluginSensor(string name, float value, string? unit = null)
        {
            Id = IdUtil.Encode(name);
            Name = name;
            Value = value;
            Unit = unit;

        }
    }
}
