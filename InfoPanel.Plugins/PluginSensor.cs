namespace InfoPanel.Plugins
{
    public class PluginSensor(string id, string name, float value, string? unit = null) : IPluginSensor
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public float Value { get; set; } = value;

        public string? Unit { get; } = unit;

        public PluginSensor(string name, float value, string? unit = null) : this(IdUtil.Encode(name), name, value, unit)
        {
        }

        public override string ToString()
        {
            if (Unit == "%" && Math.Round(Value, 1) == 100)
            {
                return "100%";
            }
            else if(Unit == "%" && Math.Round(Value, 1) == 0)
            {
                return "0%";
            }

            return $"{Math.Round(Value, 1):F1}{Unit}";
        }
    }
}
