﻿namespace InfoPanel.Plugins
{
    public interface IPlugin
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        string? ConfigFilePath { get; }
        TimeSpan UpdateInterval { get; } 
        void Initialize();
        void Load(List<IPluginContainer> containers);
        void Update();
        Task UpdateAsync(CancellationToken cancellationToken);
        void Close();
    }
}
