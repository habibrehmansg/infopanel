using InfoPanel.Plugins;

namespace InfoPanel.Monitors.PluginProxies
{
    /// <summary>
    /// Data-holder proxy for IPluginText that receives values via IPC.
    /// </summary>
    internal class ProxyPluginText(string id, string name) : IPluginText
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Value { get; set; } = "";

        public override string ToString()
        {
            return Value;
        }
    }
}
