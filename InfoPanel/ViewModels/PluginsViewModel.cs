using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.Monitors;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public class PluginsViewModel: ObservableObject
    {
        public string PluginsFolder { get; } = Globals.PluginsFolder;
        public ObservableCollection<PluginViewModel> Plugins { get; set; } = [];

        public PluginsViewModel() {

            PluginsFolder = Globals.PluginsFolder;

           foreach(var wrapper in PluginMonitor.Instance._loadedPlugins.Values)
            {
                Plugins.Add(new PluginViewModel
                {
                    FileName = wrapper.FileName,
                    Name = wrapper.Name,
                    Description = wrapper.Description,
                    ConfigFilePath = wrapper.ConfigFilePath
                });
            }
        }
    }

    public class PluginViewModel
    {
        public required string FileName { get; set; }
        public required string Name { get; set; }
        public required string Description { get; set; }
        public required string? ConfigFilePath { get; set; }
    }
}
