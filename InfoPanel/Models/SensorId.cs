using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Models
{
    public enum SensorType
    {
        HwInfo = 0,
        Libre = 1,
    }

    public abstract class SensorId
    {
    }

    public class HwInfoSensorId : SensorId
    {
        public UInt32 Id { get; set; }
        public UInt32 Instance { get; set; }
        public UInt32 EntryId { get; set; }

        public HwInfoSensorId() { }

        public HwInfoSensorId(UInt32 id, UInt32 instance, UInt32 entryId)
        {
            Id = id;
            Instance = instance;
            EntryId = entryId;
        }
    }

    public class LibreSensorId : SensorId
    {
        public string SensorIdValue { get; set; }

        public LibreSensorId() { }

        public LibreSensorId(string sensorId)
        {
            SensorIdValue = sensorId;
        }
    }
}
