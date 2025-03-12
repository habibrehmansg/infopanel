using System.Collections.Generic;

namespace InfoPanel.Models
{
    public class PluginHash
    {
        public required string PluginFolder { get; set; }
        public bool Bundled { get; set; } = false;
        public bool Activated { get; set; } = false;
        public string? Hash { get; set; }
    }
}
