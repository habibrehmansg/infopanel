using InfoPanel.Plugins;
using Serilog;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InfoPanel.Extras
{
    public class ClockPlugin : BasePlugin
    {
        private readonly PluginSensor _daySensor = new("Day", 0, "");
        private readonly PluginSensor _monthSensor = new("Month", 0, "");
        private readonly PluginSensor _yearSensor = new("Year", 0, "");

        private readonly PluginSensor _12hourSensor = new("12 Hour", 0, "");
        private readonly PluginSensor _24hourSensor = new("24 Hour", 0, "");
        private readonly PluginSensor _minuteSensor = new("Minute", 0, "");
        private readonly PluginSensor _secondSensor = new("Second", 0, "");

        private readonly PluginSensor _pmSensor = new("PM", 0, "");

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(0);

        public ClockPlugin() : base("clock-plugin", "Clock", "Returns raw clock information as sensors for custom styling with Gauges etc.")
        {
        }

        public override void Initialize()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            PluginContainer container = new("Clock");
            container.Entries.Add(_daySensor);
            container.Entries.Add(_monthSensor);
            container.Entries.Add(_yearSensor);
            container.Entries.Add(_12hourSensor);
            container.Entries.Add(_24hourSensor);
            container.Entries.Add(_minuteSensor);
            container.Entries.Add(_secondSensor);
            container.Entries.Add(_pmSensor);
            containers.Add(container);
        }

        public override void Close()
        {
        }

        public override void Update()
        {
            var now = DateTime.Now;

            _daySensor.Value = now.Day;
            _monthSensor.Value = now.Month;
            _yearSensor.Value = now.Year;

            _12hourSensor.Value = now.Hour % 12 == 0 ? 12 : now.Hour % 12; // Convert to 12-hour format
            _24hourSensor.Value = now.Hour;
            _minuteSensor.Value = now.Minute;
            _secondSensor.Value = now.Second;

            _pmSensor.Value = now.Hour >= 12 ? 1 : 0; // 1 for PM, 0 for AM
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
