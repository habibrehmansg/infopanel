namespace InfoPanel.Plugins.Ipc
{
    public interface IPluginClientCallback
    {
        void OnPluginsLoaded(List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containersByPluginId);
        void OnSensorUpdate(List<SensorUpdateBatchDto> updates);
        void OnPluginError(string pluginId, string errorMessage);
        void OnPerformanceUpdate(List<PluginPerformanceDto> performances);
        void OnImageResize(string pluginId, ImageDescriptorDto descriptor);
    }
}
