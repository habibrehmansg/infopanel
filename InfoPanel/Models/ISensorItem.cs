using InfoPanel.Enums;
using System;

namespace InfoPanel.Models
{
    public enum SensorValueType
    {
        NOW, MIN, MAX, AVERAGE
    }

    internal interface ISensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        UInt32 Id { get; set; }
        UInt32 Instance { get; set; }
        UInt32 EntryId { get; set; }

        string LibreSensorId { get; set; }
        string PluginSensorId { get; set; }
        SensorValueType ValueType { get; set; }

        SensorReading? GetValue();
    }
}
