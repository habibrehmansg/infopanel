using InfoPanel.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extras
{
    internal class VolumeSensor() : IPluginSensor
    {
        public string Id => "volume";
        public string Name => "Master Volume";
        public IPluginSensorValueType ValueType => IPluginSensorValueType.Double;
        public object Value { get; set; } = 0;
        public string? Unit => "%";

    }
}
