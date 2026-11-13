namespace InfoPanel.Plugins
{
    public class PluginText(string id, string name, string value) : IPluginText
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string Value { get; set; } = value;

        public PluginText(string name, string value): this(IdUtil.Encode(name), name, value)
        {
        }

        public override string ToString()
        {
            return Value;
        }
    }
}
