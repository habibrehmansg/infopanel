using InfoPanel.Enums;
using System;

namespace InfoPanel.Models
{
    public enum SensorValueType
    {
        NOW, MIN, MAX, AVERAGE
    }

    internal interface ISensorItem: IHwInfoSensorItem, ILibreSensorItem, IPluginSensorItem
    {

    }

    internal interface IHwInfoSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue();
        UInt32 Id { get; set; }
        UInt32 Instance { get; set; }
        UInt32 EntryId { get; set; }
    }

    internal interface ILibreSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue();
        string LibreSensorId { get; set; }
    }

    internal interface IPluginSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue();
        string PluginSensorId { get; set; }
    }
}
