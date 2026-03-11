using System.Collections.Generic;
using System.Threading.Tasks;
using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Monitors
{
    /// <summary>
    /// Replaces PluginWrapper in the main app for IPC-based plugins.
    /// Stores metadata and delegates operations to the host connection.
    /// </summary>
    internal class RemotePluginWrapper
    {
        private readonly PluginHostConnection _connection;
        private readonly PluginMetadataDto _metadata;

        public RemotePluginWrapper(PluginHostConnection connection, PluginMetadataDto metadata)
        {
            _connection = connection;
            _metadata = metadata;
        }

        public string Id => _metadata.Id;
        public string Name => _metadata.Name;
        public string Description => _metadata.Description;
        public string? ConfigFilePath => _metadata.ConfigFilePath;
        public double UpdateIntervalMs => _metadata.UpdateIntervalMs;
        public List<PluginActionDto> Actions => _metadata.Actions;
        public PluginMetadataDto Metadata => _metadata;

        public bool IsRunning => _connection.IsProcessRunning && _connection.IsConnected;
        public bool IsLoaded => IsRunning;

        public long UpdateTimeMilliseconds => _connection.GetUpdateTimeMilliseconds(_metadata.Id);

        public async Task InvokeActionAsync(string methodName)
        {
            await _connection.InvokeActionAsync(_metadata.Id, methodName);
        }
    }
}
