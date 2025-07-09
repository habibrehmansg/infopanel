namespace InfoPanel.Plugins
{
    public class PluginContainer : IPluginContainer
    {
        public PluginContainer(string id, string name, bool isEmphemeralPath = false)
        {
            Id = id;
            Name = name;
            IsEmphemeralPath = isEmphemeralPath;
        }

        public PluginContainer(string name)
        {
            Id = IdUtil.Encode(name);
            Name = name;
            IsEmphemeralPath = false;
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsEmphemeralPath { get; }
        public List<IPluginData> Entries { get; } = [];
    }
}
