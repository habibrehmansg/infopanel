using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
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
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls.Interfaces;

namespace InfoPanel.ViewModels
{
    public partial class PluginsViewModel: ObservableObject
    {
        public string PluginsFolder { get; }
        public ObservableCollection<PluginDisplayModel> EnabledDisplayPlugins { get; } = new();

        [ObservableProperty]
        private Visibility _showModifiedHashWarning = Visibility.Collapsed;

        [ObservableProperty]
        private Visibility _showRestartBanner = Visibility.Collapsed;

        public PluginsViewModel() {
            PluginsFolder = PluginStateHelper.PluginsFolder;
            RefreshPlugins();
        }

        [RelayCommand]
        public void RefreshPlugins()
        {
            EnabledDisplayPlugins.Clear();
            GetEnabledPluginList();        
        }

        [RelayCommand]
        public void UpdateAndReloadPlugins()
        {
            UpdatePluginStateFile();
            ShowRestartBanner = Visibility.Visible;
        }


        private void GetEnabledPluginList()
        {
            var validationState = PluginStateHelper.ValidateHashes();
            var modifiedList = validationState.Item2;

            foreach (var folder in Directory.GetDirectories(PluginStateHelper.PluginsFolder)) {
                var folderName = Path.GetFileName(folder);

                var hash = PluginStateHelper.HashPlugin(folderName);


                var files = Directory.GetFiles(folder);

                if (!files.Contains(Path.Combine(folder, folderName + ".dll")))
                {
                    continue;
                }

                var model = new PluginDisplayModel { Name = Path.GetFileName(folder) };

                var modifiedHash = modifiedList.Find(x => x.PluginName == folderName);
                model.Modified = modifiedHash != null;

                foreach(var plugin in PluginMonitor.Instance._loadedPlugins.Values.Where(x => x.FileName == folderName + ".dll"))
                {
                    model.Plugins.Add(new PluginViewModel
                    {
                        FileName = plugin.FileName,
                        Name = plugin.Name,
                        Description = plugin.Description,
                        ConfigFilePath = plugin.ConfigFilePath
                    });
                }

                model.Activated = model.Plugins.Count > 0;

                EnabledDisplayPlugins.Add(model);
            }
        }

        public void UpdatePluginStateFile()
        {
            var allPlugins = new List<PluginDisplayModel>();
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
                Filter = "InfoPanel Plugin Archive |*.zip",
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
                            za.ExtractToDirectory(PluginStateHelper.PluginsFolder, true);
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
        public required string Name { get; set; }
        public bool Activated { get; set; } = false;
        public bool Modified { get; set; } = false;
        public List<PluginViewModel> Plugins { get; set; } = [];
    }
}
