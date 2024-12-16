using InfoPanel.Monitors;
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Media.Capture.Core;

namespace InfoPanel.Utils
{
    internal class SensorMapping
    {
        public static readonly Dictionary<string, (SensorType, HardwareType)> SensorPanel2 = new()
        {
            { "FCPU", (SensorType.Fan, HardwareType.Motherboard) },
            { "TCPU", (SensorType.Temperature, HardwareType.Cpu) },
            { "TGPU1GPU2", (SensorType.Temperature, HardwareType.GpuNvidia) },
            { "PCPUPKG", (SensorType.Power, HardwareType.Cpu) },
            { "VCPU", (SensorType.Voltage, HardwareType.Cpu) },
            { "SCPUCLK", (SensorType.Clock, HardwareType.Cpu) },
            { "TCPUDIO", (SensorType.Temperature, HardwareType.Cpu) },
            { "FGPU1", (SensorType.Fan, HardwareType.GpuNvidia) },
            { "SGPU1CLK", (SensorType.Clock, HardwareType.GpuNvidia) },
            { "SCPUUTI",(SensorType.Load, HardwareType.Cpu) },
            { "SGPU1UTI", (SensorType.Load, HardwareType.GpuNvidia) },
            { "SGPU1USEDDEMEM", (SensorType.SmallData, HardwareType.GpuNvidia)},
            { "TGPU1", (SensorType.Temperature, HardwareType.GpuNvidia) },
            { "PGPU1", (SensorType.Power, HardwareType.GpuNvidia)},
            { "SMEMUTI", (SensorType.Load, HardwareType.Memory) },
            { "SUSEDMEM", (SensorType.Data, HardwareType.Memory) },
            { "SFREEMEM", (SensorType.Data, HardwareType.Memory) },
            { "SVMEMUSAGE", (SensorType.Load, HardwareType.GpuNvidia) },
            { "TVRM", (SensorType.Temperature, HardwareType.Motherboard) },
            { "TDIMMTS2",  (SensorType.Temperature, HardwareType.Memory) }
    };

        public static string? FindMatchingIdentifier(string sensorPanelKey)
        {
            if(sensorPanelKey.Length < 1)
            {
                return null;
            }

            SensorType? sensorType = null;
            HardwareType? hardwareType = null;

            if (SensorPanel2.TryGetValue(sensorPanelKey, out (SensorType sensorType, HardwareType hardwareType) value))
            {
                sensorType = value.sensorType;
                hardwareType = value.hardwareType;
            }
            else
            {
                switch (sensorPanelKey.First())
                {
                    case 'T':
                        sensorType = SensorType.Temperature;
                        break;
                    case 'F':
                        sensorType = SensorType.Fan;
                        break;
                    case 'P':
                        sensorType = SensorType.Power;
                        break;
                    case 'V':
                        sensorType = SensorType.Voltage;
                        break;
                    case 'S':
                        sensorType = SensorType.Load;
                        break;
                }

                if(sensorPanelKey.EndsWith("CLK"))
                {
                    sensorType = SensorType.Clock;
                }else if (sensorPanelKey.EndsWith("UTI"))
                {
                    sensorType = SensorType.Load;
                }else if (sensorPanelKey.EndsWith("SPEED"))
                {
                    sensorType = SensorType.Frequency;
                }else if(sensorPanelKey.EndsWith("MUL"))
                {
                    sensorType = SensorType.Factor;
                }
                else if (sensorPanelKey.EndsWith("RATE"))
                {
                    sensorType = SensorType.Throughput;
                }

                if (sensorPanelKey[1..].StartsWith("CPU"))
                {
                    hardwareType = HardwareType.Cpu;
                }
                else if (sensorPanelKey[1..].StartsWith("MOBO"))
                {
                    hardwareType = HardwareType.Motherboard;
                }
                else if (sensorPanelKey[1..].StartsWith("GPU"))
                {
                    hardwareType = HardwareType.GpuNvidia;
                }
                else if (sensorPanelKey[1..].StartsWith("MEM") || sensorPanelKey[1..].StartsWith("DIMM"))
                {
                    hardwareType = HardwareType.Memory;
                }
                else if (sensorPanelKey[1..].StartsWith("HDD"))
                {
                    hardwareType = HardwareType.Storage;
                }
                else if (sensorPanelKey[1..].StartsWith("NIC"))
                {
                    hardwareType = HardwareType.Network;
                }
            }

            foreach (var item in LibreMonitor.GetOrderedList())
            {
                // Check if the hardware type is GPU (either Nvidia or AMD)
                bool isGpuCheck = hardwareType == HardwareType.GpuNvidia &&
                                  (item.Hardware.HardwareType == HardwareType.GpuNvidia || item.Hardware.HardwareType == HardwareType.GpuAmd);

                // Check if the hardware type matches and the sensor type matches
                bool isHardwareAndSensorTypeMatch = item.Hardware.HardwareType == hardwareType &&
                                                    item.SensorType == sensorType;

                if ((isGpuCheck || isHardwareAndSensorTypeMatch) && item.SensorType == sensorType)
                {
                    //blacklist non-frequent top level items
                    if (item.Name != "Bus Speed" && item.Name != "Core Max" && item.Name != "CPU Core Max")
                    {
                        return item.Identifier.ToString();
                    }
                }
            }
            return null;
        }
    }
}
