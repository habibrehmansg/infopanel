namespace InfoPanel.Plugins.Ipc
{
    public interface IPluginHostService
    {
        Task<List<PluginMetadataDto>> InitializeAsync();
        Task InvokeActionAsync(string pluginId, string methodName);
        Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId);
        Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value);
        Task ShutdownAsync();
    }
}
