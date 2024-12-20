using InfoPanel.Plugins;

namespace InfoPanel.Extras
{
    public class DriveInfoPlugin : BasePlugin
    {
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        private readonly List<PluginContainer> _containers = [];

        public DriveInfoPlugin() : base("Drive Info Plugin")
        {
        }

        public override void Close()
        {
            throw new NotImplementedException();
        }

        public override void Initialize()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    PluginContainer container = new(drive.Name);
                    container.Text.Add(new PluginText("name", "Name", drive.Name));
                    container.Text.Add(new PluginText("type", "Type", drive.DriveType.ToString()));
                    container.Text.Add(new PluginText("volume_label", "Volume Label", drive.VolumeLabel));
                    container.Text.Add(new PluginText("format", "Format", drive.DriveFormat));
                    container.Sensors.Add(new PluginSensor("total_size", "Total Size", drive.TotalSize / 1024 / 1024, "MB"));
                    container.Sensors.Add(new PluginSensor("free_space", "Free Space", drive.TotalFreeSpace / 1024 / 1024, "MB"));
                    container.Sensors.Add(new PluginSensor("available_space", "Available Space", drive.AvailableFreeSpace / 1024 / 1024, "MB"));
                    container.Sensors.Add(new PluginSensor("used_space", "Used Space", (drive.TotalSize - drive.TotalFreeSpace) / 1024 / 1024, "MB"));

                    _containers.Add(container);
                }
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {
            containers.AddRange(_containers);
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            foreach (var container in _containers)
            {
                DriveInfo drive = new(container.Name);

                if (drive.IsReady)
                {
                    foreach (var text in container.Text)
                    {
                        switch (text.Id)
                        {
                            case "type":
                                text.Value = drive.DriveType.ToString();
                                break;
                            case "volume_label":
                                text.Value = drive.VolumeLabel;
                                break;
                            case "format":
                                text.Value = drive.DriveFormat;
                                break;
                        }
                    }

                    foreach (var sensor in container.Sensors)
                    {
                        switch (sensor.Id)
                        {
                            case "total_size":
                                sensor.Value = drive.TotalSize / 1024 / 1024;
                                break;
                            case "free_space":
                                sensor.Value = drive.TotalFreeSpace / 1024 / 1024;
                                break;
                            case "available_space":
                                sensor.Value = drive.AvailableFreeSpace / 1024 / 1024;
                                break;
                            case "used_space":
                                sensor.Value = (drive.TotalSize - drive.TotalFreeSpace) / 1024 / 1024;
                                break;
                        }
                    }
                }
            }

            return Task.CompletedTask;
        }
    }
}
