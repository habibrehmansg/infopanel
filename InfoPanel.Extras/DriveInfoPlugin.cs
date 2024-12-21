using InfoPanel.Plugins;

namespace InfoPanel.Extras
{
    public class DriveInfoPlugin : BasePlugin
    {
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override string? ConfigFilePath => null;

        private readonly List<PluginContainer> _containers = [];

        public DriveInfoPlugin() : base("drive-info-plugin", "Drive Info", "Retrieves local disk space information.")
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
                    PluginContainer container = new(drive.Name.TrimEnd('\\'));
                    container.Entries.Add(new PluginText("name", "Name", drive.Name.TrimEnd('\\')));
                    container.Entries.Add(new PluginText("type", "Type", drive.DriveType.ToString()));
                    container.Entries.Add(new PluginText("volume_label", "Volume Label", drive.VolumeLabel));
                    container.Entries.Add(new PluginText("format", "Format", drive.DriveFormat));
                    container.Entries.Add(new PluginSensor("total_size", "Total Size", drive.TotalSize / 1024 / 1024, "MB"));
                    container.Entries.Add(new PluginSensor("free_space", "Free Space", drive.TotalFreeSpace / 1024 / 1024, "MB"));
                    container.Entries.Add(new PluginSensor("available_space", "Available Space", drive.AvailableFreeSpace / 1024 / 1024, "MB"));
                    container.Entries.Add(new PluginSensor("used_space", "Used Space", (drive.TotalSize - drive.TotalFreeSpace) / 1024 / 1024, "MB"));

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
                    foreach (var entry in container.Entries)
                    {
                        switch (entry.Id)
                        {
                            case "type":
                                {
                                    if (entry is PluginText text)
                                    {
                                        text.Value = drive.DriveType.ToString();
                                    }
                                }
                                break;
                            case "volume_label":
                                {
                                    if (entry is PluginText text)
                                    {
                                        text.Value = drive.VolumeLabel;
                                    }
                                }
                                break;
                            case "format":
                                {
                                    if (entry is PluginText text)
                                    {
                                        text.Value = drive.DriveFormat;
                                    }
                                }
                                break;
                            case "total_size":
                                {
                                    if (entry is PluginSensor sensor)
                                    {
                                        sensor.Value = drive.TotalSize / 1024 / 1024;
                                    }
                                }
                                break;
                            case "free_space":
                                {
                                    if (entry is PluginSensor sensor)
                                    {
                                        sensor.Value = drive.TotalFreeSpace / 1024 / 1024;
                                    }
                                }
                                break;
                            case "available_space":
                                {
                                    if (entry is PluginSensor sensor)
                                    {
                                        sensor.Value = drive.AvailableFreeSpace / 1024 / 1024;
                                    }
                                }
                                break;
                            case "used_space":
                                {
                                    if (entry is PluginSensor sensor)
                                    {
                                        sensor.Value = (drive.TotalSize - drive.TotalFreeSpace) / 1024 / 1024;
                                    }
                                }
                                break;
                        }
                    }
                }
            }

            return Task.CompletedTask;
        }
    }
}
