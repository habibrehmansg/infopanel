using InfoPanel.Plugins;
using System.Diagnostics;

namespace InfoPanel.Extras
{
    public class IpifyPlugin : BasePlugin
    {
        private readonly Stopwatch _stopwatch = new();
        private readonly PluginText _ipv4Sensor = new("IPv4", "-");
        private readonly PluginText _ipv6Sensor = new("IPv6", "-");
        private readonly HttpClient _httpClient = new();

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(5);

        public IpifyPlugin() : base("ipify-plugin", "Public IP - Ipify", "IPv4 & IPv6 lookup via ipify.org API.")
        {
        }

        public override void Initialize()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {   
            var container = new PluginContainer("Public IP");
            container.Entries.Add(_ipv4Sensor);
            container.Entries.Add(_ipv6Sensor);
            containers.Add(container);
        }

        public override void Close()
        {
            _httpClient.Dispose();
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            if (!_stopwatch.IsRunning || _stopwatch.ElapsedMilliseconds > 60000)
            {
                Trace.WriteLine("IpifyPlugin: Getting IP");
                await GetIp(cancellationToken);
                _stopwatch.Restart();
            }
        }

        private async Task GetIp(CancellationToken cancellationToken)
        {
            try
            {
                var ipv4 = await _httpClient.GetStringAsync("https://api.ipify.org", cancellationToken);
                _ipv4Sensor.Value = ipv4;
            }
            catch
            {
                Trace.WriteLine("IpifyPlugin: Failed to get IPv6");
            }

            try
            {
                var ipv6 = await _httpClient.GetStringAsync("https://api6.ipify.org", cancellationToken);
                _ipv6Sensor.Value = ipv6;
            }
            catch
            {
                Trace.WriteLine("IpifyPlugin: Failed to get IPv6");
            }
        }
    }
}
