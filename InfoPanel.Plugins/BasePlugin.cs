namespace InfoPanel.Plugins
{
    public abstract class BasePlugin(string id, string name, string description) : IPlugin
    {
        public string Id { get; } = id;
        public string Name { get; } = name;

        public string Description { get; } = description;

        public abstract string? ConfigFilePath { get; }
        public abstract TimeSpan UpdateInterval { get; }

        public BasePlugin(string name, string description = "") : this(IdUtil.Encode(name), name, description)
        {
        }

        public abstract void Close();
        public abstract void Initialize();
        public abstract void Load(List<IPluginContainer> containers);
        public abstract void Update();
        public abstract Task UpdateAsync(CancellationToken cancellationToken);
    }
}
