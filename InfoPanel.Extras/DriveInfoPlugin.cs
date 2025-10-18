using InfoPanel.Plugins;

namespace InfoPanel.Extras
{
    public class DriveInfoPlugin : BasePlugin
    {
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

        public override string? ConfigFilePath => null;

        private readonly List<PluginContainer> _containers = [];
        private readonly Dictionary<string, DriveInfo> _driveCache = [];

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
                    try
                    {
                        string driveName = drive.Name.TrimEnd('\\');
                        PluginContainer container = new(driveName);

                        container.Entries.Add(new PluginText("name", "Name", driveName));
                        container.Entries.Add(new PluginText("type", "Type", drive.DriveType.ToString()));
                        container.Entries.Add(new PluginText("volume_label", "Volume Label", drive.VolumeLabel));
                        container.Entries.Add(new PluginText("format", "Format", drive.DriveFormat));
                        container.Entries.Add(new PluginSensor("total_size", "Total Size", drive.TotalSize / 1024 / 1024, "MB"));
                        container.Entries.Add(new PluginSensor("free_space", "Free Space", drive.TotalFreeSpace / 1024 / 1024, "MB"));
                        container.Entries.Add(new PluginSensor("available_space", "Available Space", drive.AvailableFreeSpace / 1024 / 1024, "MB"));
                        container.Entries.Add(new PluginSensor("used_space", "Used Space", (drive.TotalSize - drive.TotalFreeSpace) / 1024 / 1024, "MB"));

                        _containers.Add(container);
                        _driveCache[driveName] = drive;
                    }
                    catch
                    {
                        // Skip drives that can't be accessed
                    }
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
                // Use cached DriveInfo instance instead of creating a new one
                if (!_driveCache.TryGetValue(container.Name, out var drive))
                {
                    continue;
                }

                try
                {
                    // Check if drive is still ready before accessing properties
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    // Only update space-related properties (these change frequently)
                    // Static properties (type, volume_label, format) are set once during initialization
                    foreach (var entry in container.Entries)
                    {
                        if (entry is not PluginSensor sensor)
                        {
                            continue;
                        }

                        switch (entry.Id)
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
                catch
                {
                    // Skip drives that become unavailable or throw exceptions
                }
            }

            return Task.CompletedTask;
        }
    }
}
