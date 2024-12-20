namespace InfoPanel.Plugins
{
    public class PluginContainer(string name) : IPluginContainer
    {
        public string Id { get; } = IdUtil.Encode(name);
        public string Name { get; } = name;
        public List<IPluginData> Entries { get; } = [];
    }
}
