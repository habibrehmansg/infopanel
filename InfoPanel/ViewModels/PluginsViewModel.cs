using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using Wpf.Ui.Controls.Interfaces;

namespace InfoPanel.ViewModels
{
    public partial class PluginsViewModel: ObservableObject
    {
        public string PluginsFolder { get; }
        public ObservableCollection<PluginDisplayModel> AvailableDisplayPlugins { get; } = new();
        public ObservableCollection<PluginDisplayModel> EnabledDisplayPlugins { get; } = new();

        public PluginsViewModel() {
            PluginsFolder = PluginStateHelper.PluginsFolder;
            RefreshPlugins();
        }

        [RelayCommand]
        public void RefreshPlugins()
        {
            AvailableDisplayPlugins.Clear();
            EnabledDisplayPlugins.Clear();
            GetAvailablePluginList();
            GetEnabledPluginList();
        }

        private void GetAvailablePluginList()
        {
            var localPluginList = PluginStateHelper.GetLocalPluginDllHashes();
            var pluginStateList = PluginStateHelper.DecryptAndLoadStateList().Where(x => x.Activated == true);
            localPluginList = localPluginList.Where(x => !pluginStateList.Any(sl => sl.PluginName == x.PluginName)).ToList();
            foreach (var plugin in localPluginList)
            {
                AvailableDisplayPlugins.Add(new PluginDisplayModel
                {
                    Name = plugin.PluginName,
                    Activated = false,
                    Plugins = new List<PluginViewModel>
                    {
                        new PluginViewModel
                        {
                            Name = plugin.PluginName,
                            FileName = string.Empty,
                            Description = string.Empty,
                            ConfigFilePath = null
                        }
                    }
                });
            }
        }

        private void GetEnabledPluginList()
        {
            var groupedPlugins = PluginMonitor.Instance._loadedPlugins.Values.GroupBy(x => x.FileName);
            foreach (var group in groupedPlugins)
            {
                var pluginList = new List<PluginViewModel>();
                foreach (var plugin in group)
                {
                    pluginList.Add(new PluginViewModel
                    {
                        FileName = plugin.FileName,
                        Name = plugin.Name,
                        Description = plugin.Description,
                        ConfigFilePath = plugin.ConfigFilePath
                    });
                }
                EnabledDisplayPlugins.Add(new PluginDisplayModel { Name = Path.GetFileNameWithoutExtension(group.Key), Activated = true, Plugins = pluginList });
            }
        }

        [RelayCommand]
        public void UpdatePluginStateFile()
        {
            var allPlugins = new List<PluginDisplayModel>();
            allPlugins.AddRange(AvailableDisplayPlugins);
            allPlugins.AddRange(EnabledDisplayPlugins);
            var localPluginList = PluginStateHelper.GetLocalPluginDllHashes();

            var pluginHashList = new List<PluginHash>();
            foreach (var localPlugin in localPluginList)
            {
                var pluginModel = allPlugins.FirstOrDefault(x => x.Name == localPlugin.PluginName);
                if (pluginModel != null)
                {
                    localPlugin.Activated = pluginModel.Activated;                    
                    pluginHashList.Add(localPlugin);
                }
            }
            PluginStateHelper.UpdatePluginStateList(pluginHashList);
        }

        [RelayCommand]
        public static void AddPluginFromZip()
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new()
            {
                Multiselect = false,
                Filter = "InfoPanel Plugin Files |*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (openFileDialog.ShowDialog() == true)
            {
                var pluginFilePath = openFileDialog.FileName;

                using (var fs = new FileStream(pluginFilePath, FileMode.Open))
                {
                    using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        var entry = za.Entries[0];
                        if (Regex.IsMatch(entry.FullName, "InfoPanel.[a-zA-Z0-9]+\\/"))
                        {
                            za.ExtractToDirectory(PluginStateHelper.PluginsFolder);
                        }
                    }
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
    }

    public class PluginDisplayModel
    {
        public string Name { get; set; }
        public bool Activated { get; set; } = false;
        public List<PluginViewModel> Plugins { get; set; } = new();
    }
}
