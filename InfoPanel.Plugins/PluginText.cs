namespace InfoPanel.Plugins
{
    public class PluginText : IPluginText
    {
        public string Id { get; }

        public string Name { get; }

        public string Value { get; set; }

        public PluginText(string id, string name, string value)
        {
            Id = id;
            Name = name;
            Value = value;
        }

        public PluginText(string name, string value)
        {
            Id = IdUtil.Encode(name);
            Name = name;
            Value = value;
        }
    }
}
