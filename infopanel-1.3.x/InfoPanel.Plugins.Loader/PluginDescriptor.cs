using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Plugins.Loader
{
    public class PluginDescriptor(string filePath, PluginInfo? pluginInfo)
    {
        public string FilePath { get; } = filePath;
        public string FileName => Path.GetFileName(FilePath);
        public string? FolderPath => Path.GetDirectoryName(FilePath);
        public string? FolderName => Path.GetFileName(FolderPath);
        public PluginInfo? PluginInfo { get; set; } = pluginInfo;

        public readonly Dictionary<string, PluginWrapper> PluginWrappers = [];
    }
}
