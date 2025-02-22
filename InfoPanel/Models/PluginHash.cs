using System.Collections.Generic;

namespace InfoPanel.Models
{
    public class PluginHash
    {
        public string PluginName { get; set; } = string.Empty;
        public bool Activated { get; set; } = false;
        public Dictionary<string, string> Hashes { get; set; } = [];

    }
}
