namespace InfoPanel.Plugins
{
    public class PluginContainer : IPluginContainer
    {
        public PluginContainer(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public PluginContainer(string name)
        {
            Id = IdUtil.Encode(name);
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }
        public List<IPluginData> Entries { get; } = [];
    }
}
