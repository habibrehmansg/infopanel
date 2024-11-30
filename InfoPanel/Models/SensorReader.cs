using InfoPanel.Monitors;
using LibreHardwareMonitor.Hardware;
using System;

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
    }
}
