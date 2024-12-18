using InfoPanel.Plugins;
using OpenWeatherMap.Standard.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extras
{
    internal class WeatherSensor(string id, string name, IPluginSensorValueType valueType, object value, string? unit = null): IPluginSensor
    {
        public string Id => id;
        public string Name => name;
        public IPluginSensorValueType ValueType => valueType;
        public object Value { get; set; } = value;
        public string? Unit => unit;
    }
}
