namespace InfoPanel.Plugins
{
    public abstract class BasePlugin : IPlugin
    {
        public string Id { get; }
        public string Name { get; }
        public abstract TimeSpan UpdateInterval { get; }

        public BasePlugin(string name)
        {
            Id = IdUtil.Encode(name);
            Name = name;
        }

        public BasePlugin(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public abstract void Close();
        public abstract void Initialize();
        public abstract void Load(List<IPluginContainer> containers);
        public abstract void Update();
        public abstract Task UpdateAsync(CancellationToken cancellationToken);
    }
}
