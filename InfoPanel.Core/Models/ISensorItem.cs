using InfoPanel.Enums;
using InfoPanel.Sensors;
using System;

namespace InfoPanel.Models
{
    public enum SensorValueType
    {
        NOW, MIN, MAX, AVERAGE
    }

    public interface ISensorItem: IHwInfoSensorItem, ILibreSensorItem, IPluginSensorItem
    {

    }

    public interface IHwInfoSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue(ISensorReader sensors);
        int HwInfoRemoteIndex { get; set; }
        UInt32 Id { get; set; }
        UInt32 Instance { get; set; }
        UInt32 EntryId { get; set; }
    }

    public interface ILibreSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue(ISensorReader sensors);
        string LibreSensorId { get; set; }
    }

    public interface IPluginSensorItem
    {
        string SensorName { get; set; }
        SensorType SensorType { get; set; }
        SensorValueType ValueType { get; set; }
        SensorReading? GetValue(ISensorReader sensors);
        string PluginSensorId { get; set; }
    }
}
