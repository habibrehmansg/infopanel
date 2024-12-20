using InfoPanel.Plugins;
using NAudio.CoreAudioApi;

namespace InfoPanel.Extras
{
    public class VolumePlugin : BasePlugin
    {
        private MMDeviceEnumerator? _deviceEnumerator;
        private readonly PluginSensor _volumeSensor = new("Master Volume", 0, "%");

        public VolumePlugin() : base("Volume Plugin")
        {
        }

        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(50);

        public override void Initialize()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }

        public override void Close()
        {
            _deviceEnumerator?.Dispose();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Default");
            container.Entries.Add(_volumeSensor);
            containers.Add(container);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }

        public override void Update()
        {
            using var defaultDevice = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _volumeSensor.Value = (float)Math.Round((defaultDevice?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0) * 100);
        }
    }
}
