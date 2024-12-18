using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Plugins
{
    public enum IPluginSensorValueType
    {
        Double,
        String
    }

    public interface IPluginSensor
    {
        string Id { get; }
        string Name { get; }
        IPluginSensorValueType ValueType { get; }
        object Value { get; set; }
        string? Unit { get; }
    }
}
