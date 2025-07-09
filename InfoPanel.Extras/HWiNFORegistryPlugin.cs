using InfoPanel.Plugins;
using Microsoft.Win32;
using System.Collections.Concurrent;

namespace InfoPanel.Extras
{
    public class HWiNFORegistryPlugin : BasePlugin
    {
        public override string? ConfigFilePath => null;

        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        private readonly ConcurrentDictionary<string, PluginContainer> _containers = [];
        private string _registryPath = @"SOFTWARE\HWiNFO64\VSB";

        public HWiNFORegistryPlugin() : base("hwinfo-registry-plugin", "HWiNFO Registry", "Alternate access to HWiNFO via registry.")
        {
        }

        public override void Initialize()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath)
                                 ?? Registry.LocalMachine.OpenSubKey(_registryPath);

            if (key != null)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    if (valueName.StartsWith("Sensor"))
                    {
                        string index = valueName[6..]; // Remove "Sensor"

                        if (key.GetValue($"Sensor{index}") is string sensorValue
                        && key.GetValue($"Label{index}") is string labelValue
                        && key.GetValue($"Value{index}") is string value
                        && key.GetValue($"ValueRaw{index}") is string valueRaw
                        )
                        {
                            PluginContainer container = _containers.GetOrAdd(sensorValue, _ => new PluginContainer(sensorValue, sensorValue, true));

                            if (float.TryParse(valueRaw, out float parsedValue))
                            {
                                var unit = value.Length > valueRaw.Length ? value[valueRaw.Length..] : ""; // Extract unit from value
                                container.Entries.Add(new PluginSensor($"{index}", labelValue, parsedValue, unit)); // Assuming percentage for simplicity
                            }
                        }

                    }
                }
            }

            containers.AddRange(_containers.Values);
        }

        public override void Update()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath)
                               ?? Registry.LocalMachine.OpenSubKey(_registryPath);

            if (key != null)
            {
                foreach (var container in _containers)
                {
                    foreach(var entry in container.Value.Entries)
                    {
                        if(entry is PluginSensor sensor)
                        {
                            if (key.GetValue($"ValueRaw{entry.Id}") is string valueRaw)
                            {
                                if (float.TryParse(valueRaw, out float parsedValue))
                                {
                                    sensor.Value = parsedValue;
                                }
                            }
                        }
                    }
                }
            }
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }
        public override void Close()
        {
            _containers.Clear();
        }
    }
}
