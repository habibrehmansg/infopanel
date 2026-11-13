namespace InfoPanel.Plugins
{
    public interface IPlugin
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        [System.Obsolete("Use IPluginConfigurable for config UI and automatic persistence. ConfigFilePath is only needed for legacy manual config file management.")]
        string? ConfigFilePath { get; }
        TimeSpan UpdateInterval { get; } 
        void Initialize();
        void Load(List<IPluginContainer> containers);
        void Update();
        Task UpdateAsync(CancellationToken cancellationToken);
        void Close();
    }
}
