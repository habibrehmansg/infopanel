using InfoPanel.Plugins;
using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace InfoPanel.Extras
{
    public class IpifyPlugin : IPlugin
    {
        private readonly Stopwatch _stopwatch = new();
        private readonly IpifySensor _ipifySensor = new();
        private readonly HttpClient _httpClient = new();

        string IPlugin.Name => "Ipify Plugin";

        void IPlugin.Initialize()
        {
        }

        void IPlugin.Close()
        {
            _httpClient.Dispose();
        }

        List<IPluginSensor> IPlugin.GetData()
        {
            return [_ipifySensor];
        }

        async Task IPlugin.UpdateAsync()
        {
            if (!_stopwatch.IsRunning || _stopwatch.ElapsedMilliseconds > 60000)
            {
                Trace.WriteLine("IpifyPlugin: Getting IP");
                await GetIp();
                _stopwatch.Restart();
            }
        }

        private async Task GetIp()
        {
            var ip = await _httpClient.GetStringAsync("https://api.ipify.org");
            _ipifySensor.Value = ip;
        }
    }
}
