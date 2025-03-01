using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
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
using System.Windows.Input;
using Wpf.Ui.Controls.Interfaces;

namespace InfoPanel.ViewModels
{
    public partial class PluginsViewModel : ObservableObject
    {
        public string PluginsFolder { get; }
        public ObservableCollection<PluginDisplayModel> EnabledDisplayPlugins { get; } = [];

        public ObservableCollection<PluginDisplayModel> ExternalPlugins { get; } = [];

        [ObservableProperty]
        private Visibility _showModifiedHashWarning = Visibility.Collapsed;

        [ObservableProperty]
        private Visibility _showRestartBanner = Visibility.Collapsed;

        public PluginsViewModel()
        {
            PluginsFolder = PluginStateHelper.PluginsFolder;
            RefreshPlugins();
        }

        [RelayCommand]
        public void RefreshPlugins()
        {
            EnabledDisplayPlugins.Clear();
            ExternalPlugins.Clear();
            GetEnabledPluginList();
            GetExternalPluginList();
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

            //foreach (var folder in Directory.GetDirectories(PluginStateHelper.PluginsFolder))
            foreach (var folder in Directory.GetDirectories("plugins"))
            {
                var folderName = Path.GetFileName(folder);

                var hash = PluginStateHelper.HashPlugin(folderName);


                var files = Directory.GetFiles(folder);

                if (!files.Contains(Path.Combine(folder, folderName + ".dll")))
                {
                    continue;
                }

                var pluginInfo = PluginLoader.GetPluginInfo(folder);

                var model = new PluginDisplayModel
                {
                    Name = pluginInfo?.Name ?? Path.GetFileName(folder),
                    Author = pluginInfo?.Author,
                    Description = pluginInfo?.Description,
                    Version = pluginInfo?.Version,
                    Website = pluginInfo?.Website
                };

                var modifiedHash = modifiedList.Find(x => x.PluginName == folderName);
                model.Modified = modifiedHash != null;

                foreach (var plugin in PluginMonitor.Instance._loadedPlugins.Values.Where(x => x.FileName == folderName + ".dll"))
                {
                    var pluginViewModel = new PluginViewModel
                    {
                        Id = plugin.Id,
                        Name = plugin.Name,
                        Description = plugin.Description,
                        ConfigFilePath = plugin.ConfigFilePath
                    };

                    var methods = plugin.Plugin.GetType().GetMethods().Where(m => m.GetCustomAttributes(typeof(PluginActionAttribute), false).Length > 0);


                    foreach (var method in methods)
                    {
                        var attribute = (PluginActionAttribute)method.GetCustomAttributes(typeof(PluginActionAttribute), false).First();
                        string displayName = attribute.DisplayName;

                        var command = new RelayCommand(() => method.Invoke(plugin.Plugin, null));
                        pluginViewModel.Actions.Add(new PluginActionCommand { DisplayName = displayName, Command = command });
                    }

                    model.Plugins.Add(pluginViewModel);
                }

                model.Activated = model.Plugins.Count > 0;

                EnabledDisplayPlugins.Add(model);
            }
        }

        private void GetExternalPluginList()
        {
            var validationState = PluginStateHelper.ValidateHashes();
            var modifiedList = validationState.Item2;

            foreach (var folder in Directory.GetDirectories(PluginStateHelper.PluginsFolder))
            {
                var folderName = Path.GetFileName(folder);

                var hash = PluginStateHelper.HashPlugin(folderName);

                var files = Directory.GetFiles(folder);

                if (!files.Contains(Path.Combine(folder, folderName + ".dll")))
                {
                    continue;
                }

                var pluginInfo = PluginLoader.GetPluginInfo(folder);

                var model = new PluginDisplayModel
                {
                    Name = pluginInfo?.Name ?? Path.GetFileName(folder),
                    Author = pluginInfo?.Author,
                    Description = pluginInfo?.Description,
                    Version = pluginInfo?.Version,
                    Website = pluginInfo?.Website
                };

                var modifiedHash = modifiedList.Find(x => x.PluginName == folderName);
                model.Modified = modifiedHash != null;

                foreach (var plugin in PluginMonitor.Instance._loadedPlugins.Values.Where(x => x.FileName == folderName + ".dll"))
                {
                    var pluginViewModel = new PluginViewModel
                    {
                        Id = plugin.Id,
                        Name = plugin.Name,
                        Description = plugin.Description,
                        ConfigFilePath = plugin.ConfigFilePath
                    };

                    var methods = plugin.Plugin.GetType().GetMethods().Where(m => m.GetCustomAttributes(typeof(PluginActionAttribute), false).Length > 0);


                    foreach (var method in methods)
                    {
                        var attribute = (PluginActionAttribute)method.GetCustomAttributes(typeof(PluginActionAttribute), false).First();
                        string displayName = attribute.DisplayName;

                        var command = new RelayCommand(() => method.Invoke(plugin.Plugin, null));
                        pluginViewModel.Actions.Add(new PluginActionCommand { DisplayName = displayName, Command = command });
                    }

                    model.Plugins.Add(pluginViewModel);
                }

                model.Activated = model.Plugins.Count > 0;

                ExternalPlugins.Add(model);
            }
        }

        public void UpdatePluginStateFile()
        {
            var allPlugins = new List<PluginDisplayModel>();
            allPlugins.AddRange(ExternalPlugins);

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

    public partial class PluginViewModel
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Description { get; set; }
        public required string? ConfigFilePath { get; set; }
        public ObservableCollection<PluginActionCommand> Actions { get; } = [];

        [RelayCommand]
        public async Task ReloadPlugin(string pluginId)
        {
            await PluginMonitor.Instance.ReloadPlugin(pluginId);
        }
    }

    public class PluginActionCommand
    {
        public required string DisplayName { get; set; }
        public required ICommand Command { get; set; }
    }

    public class PluginDisplayModel
    {
        public required string Name { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? Version { get; set; }
        public string? Website { get; set; }
        public bool Activated { get; set; } = false;
        public bool Modified { get; set; } = false;
        public List<PluginViewModel> Plugins { get; set; } = [];
    }
}
