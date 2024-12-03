using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Xml.Linq;

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

    public abstract class SensorTreeItem(object id, string name) : TreeItem(id, name) {
        private double _value;
        public double Value
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

    public partial class HwInfoSensorItem(object id, string name, UInt32 parentId, UInt32 parentInstance, UInt32 sensorId) : SensorTreeItem(id, name)
    {
        public UInt32 ParentId { get; set; } = parentId;
        public UInt32 ParentInstance { get; set; } = parentInstance;
        public UInt32 SensorId { get; set; } = sensorId;

        public override void Update()
        {
            // Update sensor value
            var sensorReading = SensorReader.ReadHwInfoSensor(ParentId, ParentInstance, SensorId);
            if(sensorReading.HasValue)
            {
                Value = sensorReading.Value.ValueNow;
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
                Value = sensorReading.Value.ValueNow;
                Unit = sensorReading.Value.Unit;
            }
        }
    }
}
