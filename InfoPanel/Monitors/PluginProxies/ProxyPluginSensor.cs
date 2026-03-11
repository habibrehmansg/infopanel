using System;
using InfoPanel.Plugins;

namespace InfoPanel.Monitors.PluginProxies
{
    /// <summary>
    /// Data-holder proxy for IPluginSensor that receives values via IPC.
    /// </summary>
    internal class ProxyPluginSensor(string id, string name, string? unit) : IPluginSensor
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public float Value { get; set; }
        public float ValueMin { get; set; }
        public float ValueMax { get; set; }
        public float ValueAvg { get; set; }
        public string? Unit { get; } = unit;

        public override string ToString()
        {
            if (Unit == "%" && Math.Round(Value, 1) == 100)
                return "100%";
            else if (Unit == "%" && Math.Round(Value, 1) == 0)
                return "0%";

            return $"{Math.Round(Value, 1):F1}{Unit}";
        }
    }
}
