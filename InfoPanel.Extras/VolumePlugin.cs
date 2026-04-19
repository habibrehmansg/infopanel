using InfoPanel.Plugins;
using NAudio.CoreAudioApi;
using System.ComponentModel;

namespace InfoPanel.Extras
{
    public class VolumePlugin : BasePlugin
    {
        private readonly List<PluginContainer> _containers = [];

        private MMDeviceEnumerator? _deviceEnumerator;

        private readonly Dictionary<string, MMDevice> _deviceCache = [];

        private string? _lastDefaultDeviceId;

        public VolumePlugin() : base("volume-plugin","Volume Info", "Retrieves audio output devices and relevant details. Powered by NAudio.")
        {
        }
        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(250);

        public override void Initialize()
        {
            //add default first
            _deviceEnumerator = new();
            using var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            PluginContainer container = new("Default");
            container.Entries.Add(new PluginText("device_friendly_name", "Device Name", defaultDevice.DeviceFriendlyName));
            container.Entries.Add(new PluginText("friendly_name", "Name", defaultDevice.FriendlyName));
            container.Entries.Add(new PluginText("short_name", "Short Name", defaultDevice.FriendlyName.Replace($"({defaultDevice.DeviceFriendlyName})", "").Trim()));
            container.Entries.Add(new PluginSensor("volume", "Volume", (float)Math.Round(defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100), "%"));
            container.Entries.Add(new PluginText("mute", "Mute", defaultDevice.AudioEndpointVolume.Mute.ToString()));
            _containers.Add(container);
            _lastDefaultDeviceId = defaultDevice.ID;

            var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                container = new(device.ID, device.FriendlyName);
                container.Entries.Add(new PluginText("device_friendly_name", "Device Name", device.DeviceFriendlyName));
                container.Entries.Add(new PluginText("friendly_name", "Name", device.FriendlyName));
                container.Entries.Add(new PluginText("short_name", "Short Name", device.FriendlyName.Replace($"({defaultDevice.DeviceFriendlyName})", "")));
                container.Entries.Add(new PluginSensor("volume", "Volume", (float)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100), "%"));
                container.Entries.Add(new PluginText("mute", "Mute", device.AudioEndpointVolume.Mute.ToString()));
                _containers.Add(container);

                _deviceCache.TryAdd(device.ID, device);
            }
        }

        public override void Close()
        {
            _deviceEnumerator?.Dispose();
            foreach (var device in _deviceCache.Values)
            {
                device.Dispose();
            }
            _deviceCache.Clear();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            containers.AddRange(_containers);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }

        public override void Update()
        {
            foreach(var container in _containers)
            {
                MMDevice? device = null;

                try
                {
                    bool isDefault = container.Name == "Default";
                    bool defaultDeviceChanged = false;

                    if (isDefault)
                    {
                        var tempDevice = _deviceEnumerator?.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (tempDevice != null)
                        {
                            defaultDeviceChanged = tempDevice.ID != _lastDefaultDeviceId;
                            _lastDefaultDeviceId = tempDevice.ID;

                            device = _deviceCache.GetValueOrDefault(tempDevice.ID);

                            if(device == null)
                            {
                                _deviceCache.TryAdd(tempDevice.ID, tempDevice);
                                device = tempDevice;
                            } else
                            {
                                tempDevice.Dispose();
                            }
                        }
                    }
                    else
                    {
                        device = _deviceCache.GetValueOrDefault(container.Id);
                    }

                    if (device != null)
                    {
                        foreach (var entry in container.Entries)
                        {
                            switch (entry.Id)
                            {
                                case "volume":
                                    {
                                        if (entry is PluginSensor sensor)
                                        {
                                            sensor.Value = (float)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                                        }
                                    }
                                    break;
                                case "mute":
                                    {
                                        if (entry is PluginText text)
                                        {
                                            text.Value = device.AudioEndpointVolume.Mute.ToString();
                                        }
                                    }
                                    break;
                                case "device_friendly_name":
                                    {
                                        if (isDefault && defaultDeviceChanged && entry is PluginText text)
                                        {
                                            text.Value = device.DeviceFriendlyName;
                                        }
                                    }
                                    break;
                                case "friendly_name":
                                    {
                                        if (isDefault && defaultDeviceChanged && entry is PluginText text)
                                        {
                                            text.Value = device.FriendlyName;
                                        }
                                    }
                                    break;
                                case "short_name":
                                    {
                                        if (isDefault && defaultDeviceChanged && entry is PluginText text)
                                        {
                                            text.Value = device.FriendlyName.Replace($"({device.DeviceFriendlyName})", "").Trim();
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                }catch { }
            }
        }
    }
}
