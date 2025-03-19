using InfoPanel.Plugins;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InfoPanel.Extras
{
    public class NetworkInfoPlugin : BasePlugin
    {
        private readonly PluginText _ipv4Sensor = new("IPv4", "-");
        private readonly PluginText _ipv6Sensor = new("IPv6", "-");
        private readonly HttpClient _httpClient = new();

        private readonly List<PluginContainer> _containers = [];

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

        public NetworkInfoPlugin() : base("network-info-plugin", "Network Info", "Retrieves network devices info. Public IP lookup powered by ipify.org API.")
        {
        }

        public override void Initialize()
        {
           foreach(NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                //skip loopback
                if(ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                PluginContainer container = new(ni.Id, ni.Name);
                container.Entries.Add(new PluginText("id", "Id", ni.Id));
                container.Entries.Add(new PluginText("name", "Name", ni.Name));
                container.Entries.Add(new PluginText("description", "Description", ni.Description));
                container.Entries.Add(new PluginText("status", "Status", ni.OperationalStatus.ToString()));
                container.Entries.Add(new PluginSensor("speed", "Speed", ni.Speed / 1000 / 1000 , "Mbps"));

                IPInterfaceProperties ipProps = ni.GetIPProperties();

                var ipv4 = ipProps.UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork).ToList();

                for(int i = 0; i < ipv4.Count; i++)
                {
                    container.Entries.Add(new PluginText($"ipv4-{i}", $"IPv4 Address", ipv4[i].Address.ToString()));
                }

                var ipv6 = ipProps.UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetworkV6).ToList();

                for (int i = 0; i < ipv6.Count; i++)
                {
                    container.Entries.Add(new PluginText($"ipv6-{i}", $"IPv6 Address", ipv6[i].Address.ToString()));
                }

                _containers.Add(container);
            }
        }

        public override void Load(List<IPluginContainer> containers)
        {   
            var container = new PluginContainer("Public IP");
            container.Entries.Add(_ipv4Sensor);
            container.Entries.Add(_ipv6Sensor);
            containers.Add(container);

            containers.AddRange(_containers);
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
            await GetIp(cancellationToken);
            GetLocalIp();
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

        private void GetLocalIp()
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(ni => ni.Id, ni => ni);

            foreach (var container in _containers)
            {
                if(networkInterfaces.TryGetValue(container.Id, out var ni))
                {
                    IPInterfaceProperties ipProps = ni.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
                    var ipv6 = ipProps.UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetworkV6).ToList();

                    foreach (var entry in container.Entries)
                    {
                        if (entry.Id.StartsWith("ipv4-") && entry is PluginText ipv4Entry)
                        {
                            string numberPart = entry.Id[5..];
                            if (int.TryParse(numberPart, out int number))
                            {
                                if(ipv4.Count > number)
                                {
                                    ipv4Entry.Value = ipv4[number].Address.ToString();
                                }
                            }

                        }
                        else if (entry.Id.StartsWith("ipv6-") && entry is PluginText ipv6Entry)
                        {
                            string numberPart = entry.Id[5..];
                            if (int.TryParse(numberPart, out int number))
                            {
                                if (ipv6.Count > number)
                                {
                                    ipv6Entry.Value = ipv6[number].Address.ToString();
                                }
                            }
                        }
                        else
                        {
                            switch (entry.Id) {
                                case "status":
                                    if(entry is PluginText status)
                                    {
                                        status.Value = ni.OperationalStatus.ToString();
                                    }
                                    break;
                                case "speed":
                                    if(entry is PluginSensor speed)
                                    {
                                        speed.Value = ni.Speed / 1000 / 1000;
                                    }
                                    break;
                                default:
                                    //do nothing
                                    break;
                            }

                        }
                    }
                }
            }
        }
    }
}
