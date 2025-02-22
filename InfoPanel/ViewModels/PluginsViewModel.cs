using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Utils;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace InfoPanel.ViewModels
{
    public partial class PluginsViewModel: ObservableObject
    {
        public string PluginsFolder { get; }
        public ObservableCollection<PluginViewModel> Plugins { get; set; } = [];
        public ObservableCollection<PluginViewModel> AvailablePlugins { get; set; } = [];

        public PluginsViewModel() {

            PluginsFolder = PluginStateHelper.PluginsFolder;

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

            var pluginStateList = PluginStateHelper.DecryptAndLoadStateList();
            foreach (var directory in Directory.GetDirectories(PluginsFolder))
            {
                var ph = pluginStateList.FirstOrDefault(x => x.PluginName == Path.GetFileName(directory));
                if (ph != null && ph.Activated == false)
                {
                    AvailablePlugins.Add(new PluginViewModel
                    {
                        Name = ph.PluginName,
                        FileName = string.Empty,
                        Description = string.Empty,
                        ConfigFilePath = string.Empty
                    });
                }
            }
        }

        [RelayCommand]
        public void SavePluginStates()
        {
            var allPlugins = new List<PluginViewModel>();
            allPlugins.AddRange(Plugins);
            allPlugins.AddRange(AvailablePlugins);
            var localPluginList = PluginStateHelper.GetLocalPluginDllHashes();

            foreach (var localPlugin in localPluginList)
            {
                var pluginModel = allPlugins.FirstOrDefault(x => x.Name == localPlugin.PluginName);
                if(pluginModel != null)
                {
                    localPlugin.Activated = pluginModel.Activated;
                    PluginStateHelper.SetPluginState(localPlugin);
                }
            }
            
        }
    }

    public class PluginViewModel
    {
        public required string FileName { get; set; }
        public required string Name { get; set; }
        public required string Description { get; set; }
        public required string? ConfigFilePath { get; set; }
        public bool Activated { get; set; } = false;
    }
}
