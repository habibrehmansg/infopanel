using InfoPanel.Plugins;
using NAudio.CoreAudioApi;

namespace InfoPanel.Extras
{
    public class VolumePlugin : IPlugin
    {
        private MMDeviceEnumerator? _deviceEnumerator;
        private readonly VolumeSensor _volumeSensor = new();

        string IPlugin.Name => "Volume Plugin";

        void IPlugin.Initialize()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }

        void IPlugin.Close()
        {
            _deviceEnumerator?.Dispose();
        }

        List<IPluginSensor> IPlugin.GetData()
        {
            return [_volumeSensor];
        }

        Task IPlugin.UpdateAsync()
        {
            using var defaultDevice = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _volumeSensor.Value = Math.Round((defaultDevice?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0) * 100);
            return Task.CompletedTask;
        }
    }
}
