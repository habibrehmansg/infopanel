using System.Collections.Generic;

namespace InfoPanel.Models
{
    public class PluginHash
    {
        public required string PluginName { get; set; }
        public bool Activated { get; set; } = false;
        public string? Hash { get; set; }

    }
}
