using InfoPanel.Plugins;
using System.IO.Ports;
using System.Text.Json;

namespace InfoPanel.SerialReader
{
    internal class SerialReaderPlugin : BasePlugin
    {
        public SerialReaderPlugin() : base("serial-reader-plugin", "Serial Reader", "Reads data from serial")
        {
        }

        public override string? ConfigFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InfoPanel", "plugins", "InfoPanel.SerialReader", "port.json");

        public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(500);
        private SerialPort _serialPort;
        private readonly PluginSensor _serialSensor = new("SerialData", 0.00f);

        public override void Close()
        {
            _serialPort.Dispose();
        }

        public override void Initialize()
        {
            if (!File.Exists(ConfigFilePath))
            {
                var portData = new Port();
                var json = JsonSerializer.Serialize(portData);
                File.WriteAllText(ConfigFilePath, json);
            }
            var config = File.ReadAllText(ConfigFilePath);
            Port port = JsonSerializer.Deserialize<Port>(config);
            _serialPort = new SerialPort(port.COMPort, port.BaudRate, port.Parity, port.DataBits, port.StopBits) { DtrEnable = true };
            _serialPort.Open();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Serial");
            container.Entries.Add(_serialSensor);
            containers.Add(container);

            
        }

        public override void Update()
        {
            var message = _serialPort.ReadLine();
            _serialSensor.Value = (float)Convert.ToDouble(message);
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            Update();
            return Task.CompletedTask;
        }

        
    }
}
