using InfoPanel.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extras
{
    internal class IpifySensor() : IPluginSensor
    {
        public string Id => "ip";
        public string Name => "Public IP";
        public IPluginSensorValueType ValueType => IPluginSensorValueType.String;
        public object Value { get; set; } = "-";
        public string? Unit => null;

    }
}
