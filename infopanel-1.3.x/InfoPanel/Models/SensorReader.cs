using InfoPanel.Extensions;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using LibreHardwareMonitor.Hardware;
using System;
using System.Text;
using System.Windows.Documents;

namespace InfoPanel.Models
{
    internal class SensorReader
    {
        public static SensorReading? ReadHwInfoSensor(UInt32 id, UInt32 instance, UInt32 entryId)
        {
            if (HWHash.SENSORHASH.TryGetValue((id, instance, entryId), out HWHash.HWINFO_HASH hash))
            {
                return new SensorReading(hash.ValueMin, hash.ValueMax, hash.ValueAvg, hash.ValueNow, hash.Unit);
            }

            return null;
        }

        public static SensorReading? ReadLibreSensor(string sensorId)
        {
            if (LibreMonitor.SENSORHASH.TryGetValue(sensorId, out ISensor? sensor))
            {
                return new SensorReading(sensor.Min ?? 0, sensor.Max ?? 0, 0, sensor.Value ?? 0, sensor.GetUnit());
            }
            return null;
        }

        public static SensorReading? ReadPluginSensor(string sensorId)
        {
            if (PluginMonitor.SENSORHASH.TryGetValue(sensorId, out PluginMonitor.PluginReading reading))
            {
                if (reading.Data is IPluginSensor sensor)
                {
                    return new SensorReading(sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg, sensor.Value, sensor.Unit ?? "");
                }
                else if (reading.Data is IPluginText text)
                {
                    return new SensorReading(text.Value);
                }else if(reading.Data is IPluginTable table)
                {
                    return new SensorReading(table.Value, table.DefaultFormat, table.ToString());
                }
            }
            return null;
        }
    }
}
