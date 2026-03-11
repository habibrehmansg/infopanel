namespace InfoPanel.Plugins.Ipc
{
    public interface IPluginHostService
    {
        Task<List<PluginMetadataDto>> InitializeAsync();
        Task InvokeActionAsync(string pluginId, string methodName);
        Task ShutdownAsync();
    }
}
