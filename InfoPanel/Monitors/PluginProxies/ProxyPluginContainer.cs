using System.Collections.Generic;
using InfoPanel.Plugins;

namespace InfoPanel.Monitors.PluginProxies
{
    /// <summary>
    /// Data-holder proxy for IPluginContainer that receives structure via IPC.
    /// </summary>
    internal class ProxyPluginContainer(string id, string name, bool isEphemeralPath) : IPluginContainer
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool IsEphemeralPath { get; } = isEphemeralPath;
        public List<IPluginData> Entries { get; } = [];
    }
}
