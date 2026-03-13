using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Extensions;
using InfoPanel.Models;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels.Components
{
    public partial class TreeItem(object id, string name): ObservableObject
    {
        public object Id { get; set; } = id;
        public string Name { get; set; } = name;
        public string? Icon { get; set; } = "pack://application:,,,/Resources/Images/Libre/empty.png";
        public ObservableCollection<TreeItem> Children { get; set; } = [];

        public TreeItem? FindChild(object id)
        {
            foreach (var child in Children)
            {
                if (child.Id.Equals(id))
                {
                    return child;
                }
                else
                {
                    var c = child.FindChild(id);

                    if (c != null)
                    {
                        return c;
                    }
                }
            }

            return null;
        }
    }

    public partial class LibreHardwareTreeItem : TreeItem 
    {
        public LibreHardwareTreeItem(object id, string name, LibreHardwareMonitor.Hardware.HardwareType hardwareType) : base(id, name)
        {
            if(GetImage(hardwareType) is string image)
            {
                Icon = "pack://application:,,,/Resources/Images/Libre/" + image;
            }
        }

        private static string? GetImage(LibreHardwareMonitor.Hardware.HardwareType hardwareType)
        {
            return hardwareType switch
            {
                HardwareType.Cpu => "cpu.png",
                HardwareType.GpuNvidia => "nvidia.png",
                HardwareType.GpuAmd => "amd.png",
                HardwareType.GpuIntel => "intel.png",
                HardwareType.Storage => "hdd.png",
                HardwareType.Motherboard => "mainboard.png",
                HardwareType.SuperIO or HardwareType.EmbeddedController => "chip.png",
                HardwareType.Memory => "ram.png",
                HardwareType.Network => "nic.png",
                HardwareType.Cooler => "fan.png",
                HardwareType.Psu => "power-supply.png",
                HardwareType.Battery => "battery.png",
                _ => "empty.png",
            };
        }
    }


    public partial class LibreGroupTreeItem : TreeItem
    {
        public LibreGroupTreeItem(object id, string name, LibreHardwareMonitor.Hardware.SensorType readingType) : base(id, name)
        {
            Icon = "pack://application:,,,/Resources/Images/Libre/" + readingType.ToString().ToLower() + ".png";
        }
    }

    public partial class HwInfoHardwareTreeItem : TreeItem
    {
        private static readonly string IconBase = "pack://application:,,,/Resources/Images/Libre/";

        public HwInfoHardwareTreeItem(object id, string name) : base(id, name)
        {
            Icon = IconBase + GetImage(name);
        }

        private static string GetImage(string name)
        {
            var upper = name.ToUpperInvariant();

            if (upper.Contains("CPU"))
                return "cpu.png";
            if (upper.Contains("NVIDIA") || upper.Contains("GEFORCE"))
                return "nvidia.png";
            if (upper.Contains("RADEON") || (upper.Contains("AMD") && upper.Contains("GPU")))
                return "amd.png";
            if (upper.Contains("INTEL") && (upper.Contains("ARC") || upper.Contains("GPU")))
                return "intel.png";
            if (upper.Contains("DRIVE") || upper.Contains("SSD") || upper.Contains("NVME") || upper.Contains("HDD"))
                return "hdd.png";
            if (upper.Contains("MOTHERBOARD") || upper.Contains("MAINBOARD"))
                return "mainboard.png";
            if (upper.Contains("MEMORY") || upper.Contains("RAM"))
                return "ram.png";
            if (upper.Contains("NETWORK") || upper.Contains("NIC") || upper.Contains("ETHERNET") || upper.Contains("WI-FI"))
                return "nic.png";
            if (upper.Contains("FAN") || upper.Contains("COOLER") || upper.Contains("AIO"))
                return "fan.png";
            if (upper.Contains("BATTERY"))
                return "battery.png";
            if (upper.Contains("PSU") || upper.Contains("POWER SUPPLY"))
                return "power-supply.png";

            return "chip.png";
        }
    }

    public partial class HwInfoGroupTreeItem : TreeItem
    {
        private static readonly string IconBase = "pack://application:,,,/Resources/Images/Libre/";

        public HwInfoGroupTreeItem(object id, string name, string readingType) : base(id, name)
        {
            Icon = IconBase + GetImage(readingType);
        }

        private static string GetImage(string readingType)
        {
            return readingType switch
            {
                "Temperature" => "temperature.png",
                "Voltage" => "voltage.png",
                "Fan" => "fan.png",
                "Current" => "current.png",
                "Power" => "power.png",
                "Frequency" => "clock.png",
                "Usage" => "load.png",
                _ => "empty.png",
            };
        }
    }

    public abstract class SensorTreeItem(object id, string name) : TreeItem(id, name) {
        private string _value = string.Empty;
        public string Value
        {
            get { return _value; }
            set { SetProperty(ref _value, value); }
        }

        private string _unit = string.Empty;
        public string Unit
        {
            get { return _unit; }
            set { SetProperty(ref _unit, value); }
        }

        public abstract void Update();
    }

    public partial class HwInfoSensorItem(object id, string name, int remoteIndex, UInt32 parentId, UInt32 parentInstance, UInt32 sensorId) : SensorTreeItem(id, name)
    {
        public int RemoteIndex { get; set; } = remoteIndex;
        public UInt32 ParentId { get; set; } = parentId;
        public UInt32 ParentInstance { get; set; } = parentInstance;
        public UInt32 SensorId { get; set; } = sensorId;

        public override void Update()
        {
            // Update sensor value
            var sensorReading = SensorReader.ReadHwInfoSensor(RemoteIndex, ParentId, ParentInstance, SensorId);
            if(sensorReading.HasValue)
            {
                Value = sensorReading.Value.ValueNow.ToFormattedString();
                Unit = sensorReading.Value.Unit;
            }
        }
    }

    public partial class LibreSensorItem(object id, string name, string sensorId) : SensorTreeItem(id, name)
    {
        public string SensorId { get; set; } = sensorId;

        public override void Update()
        {
            // Update sensor value
            var sensorReading = SensorReader.ReadLibreSensor(SensorId);
            if (sensorReading.HasValue)
            {
                Value = sensorReading.Value.ValueNow.ToFormattedString();
                Unit = sensorReading.Value.Unit;
            }
        }
    }

    public class PluginTreeItem(object id, string name) :TreeItem(id, name)
    {
        
    }
   

    public partial class PluginSensorItem(object id, string name, string sensorId) : SensorTreeItem(id, name)
    {
        public string SensorId { get; set; } = sensorId;

        public SensorReading? SensorReading => SensorReader.ReadPluginSensor(SensorId);

        public override void Update()
        {
            var sensorReading = SensorReading;
            //Update sensor value
            if (sensorReading.HasValue)
            {
                Value = sensorReading.Value.ValueText ?? sensorReading.Value.ValueNow.ToFormattedString();
                Unit = sensorReading.Value.Unit;
            }
        }
    }
}
