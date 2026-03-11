using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Extensions;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace InfoPanel.ViewModels
{
    public partial class PluginsViewModel : ObservableObject
    {
        private readonly DispatcherTimer _timer;

        [ObservableProperty]
        private string _pluginFolder = FileUtil.GetExternalPluginFolder();

        [ObservableProperty]
        private bool _showRestartBanner = false;

        public ObservableCollection<PluginViewModel> BundledPlugins { get; } = [];

        public ObservableCollection<PluginViewModel> ExternalPlugins { get; } = [];

        public PluginsViewModel()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            BuildPluginModels();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            BuildPluginModels();
        }

        private void BuildPluginModels()
        {
            foreach (var pluginDescriptor in PluginMonitor.Instance.Plugins)
            {
                if (pluginDescriptor.FolderPath?.IsSubdirectoryOf(FileUtil.GetBundledPluginFolder()) ?? false)
                {
                    var model = BundledPlugins.SingleOrDefault(x => x.FilePath == pluginDescriptor.FilePath);

                    if (model != null)
                    {
                        model.Refresh();
                    }
                    else
                    {
                        model = new PluginViewModel(pluginDescriptor);
                        BundledPlugins.Add(model);
                    }
                }
                else
                {
                    var model = ExternalPlugins.SingleOrDefault(x => x.FilePath == pluginDescriptor.FilePath);

                    if (model != null)
                    {
                        model.Refresh();
                    }
                    else
                    {
                        model = new PluginViewModel(pluginDescriptor);
                        ExternalPlugins.Add(model);
                    }
                }
            }
        }


        [RelayCommand]
        public void AddPluginFromZip()
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new()
            {
                Multiselect = false,
                Filter = "InfoPanel Plugin Archive |InfoPanel.*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (openFileDialog.ShowDialog() == true)
            {
                var pluginFilePath = openFileDialog.FileName;

                using var fs = new FileStream(pluginFilePath, FileMode.Open);
                using var za = new ZipArchive(fs, ZipArchiveMode.Read);
                var entry = za.Entries[0];
                if (Regex.IsMatch(entry.FullName, "InfoPanel.[a-zA-Z0-9]+\\/"))
                {
                    try
                    {
                        File.Copy(openFileDialog.FileName, Path.Combine(FileUtil.GetExternalPluginFolder(), openFileDialog.SafeFileName), true);
                        ShowRestartBanner = true;
                    }catch { }
                }
            }
        }

    }

    public partial class PluginModuleViewModel : ObservableObject
    {
        private readonly RemotePluginWrapper _wrapper;
        private readonly PluginDescriptor _pluginDescriptor;
        public string Id { get; set; }

        [ObservableProperty]
        private string _name;
        [ObservableProperty]
        private string _description;
        [ObservableProperty]
        private string? _configFilePath;

        public ObservableCollection<PluginActionCommand> Actions { get; } = [];

        [RelayCommand]
        public async Task Reload()
        {
            await PluginMonitor.Instance.ReloadPluginModule(_pluginDescriptor);
        }

        internal PluginModuleViewModel(RemotePluginWrapper wrapper, PluginDescriptor pluginDescriptor)
        {
            _wrapper = wrapper;
            _pluginDescriptor = pluginDescriptor;
            Id = wrapper.Id;
            Name = wrapper.Name;
            Description = wrapper.Description;
            ConfigFilePath = wrapper.ConfigFilePath;

            foreach (var action in wrapper.Actions)
            {
                var methodName = action.MethodName;
                var command = new RelayCommand(() => _ = wrapper.InvokeActionAsync(methodName));
                Actions.Add(new PluginActionCommand { DisplayName = action.DisplayName, Command = command });
            }
        }

        public void Refresh()
        {
            Id = _wrapper.Id;
            Name = _wrapper.Name;
            Description = _wrapper.Description;
            ConfigFilePath = _wrapper.ConfigFilePath;
        }
    }

    public class PluginActionCommand
    {
        public required string DisplayName { get; set; }
        public required ICommand Command { get; set; }
    }

    public partial class PluginViewModel : ObservableObject
    {
        private PluginDescriptor _pluginDescriptor;
        public string FilePath { get; set; }
        [ObservableProperty]
        private string _name;
        [ObservableProperty]
        private string? _description;
        [ObservableProperty]
        private string? _author;
        [ObservableProperty]
        private string? _version;
        [ObservableProperty]
        private string? _website;


        private bool _activated;
        public bool Activated
        {
            get => _activated;
            set
            {
                if (SetProperty(ref _activated, value))
                {
                    _ = OnActivatedChanged();
                }
            }
        }

        public ObservableCollection<PluginModuleViewModel> Plugins { get; set; } = [];

        [ObservableProperty]
        private bool _controlEnabled = true;


        private async Task OnActivatedChanged()
        {
            ControlEnabled = false;
            if (!_activated)
            {
                await PluginMonitor.Instance.StopPluginModulesAsync(_pluginDescriptor);
            }
            else
            {
                await PluginMonitor.Instance.StartPluginModulesAsync(_pluginDescriptor);
            }

            PluginMonitor.Instance.SavePluginState();
            ControlEnabled = true;
        }

        public PluginViewModel(PluginDescriptor pluginDescriptor)
        {
            _pluginDescriptor = pluginDescriptor;

            FilePath = pluginDescriptor.FilePath;
            Name = pluginDescriptor.PluginInfo?.Name ?? pluginDescriptor.FolderName ?? pluginDescriptor.FileName;
            Author = pluginDescriptor.PluginInfo?.Author;
            Description = pluginDescriptor.PluginInfo?.Description;
            Version = pluginDescriptor.PluginInfo?.Version;
            Website = pluginDescriptor.PluginInfo?.Website;
            _activated = PluginMonitor.Instance.ProcessManager.IsHostRunning(pluginDescriptor.FilePath);

            if (PluginMonitor.Instance.RemoteWrappers.TryGetValue(pluginDescriptor.FilePath, out var wrappers))
            {
                foreach (var wrapper in wrappers)
                {
                    Plugins.Add(new PluginModuleViewModel(wrapper, pluginDescriptor));
                }
            }
        }

        public void Refresh()
        {
            if (!ControlEnabled) { return; }

            _activated = PluginMonitor.Instance.ProcessManager.IsHostRunning(_pluginDescriptor.FilePath);
            OnPropertyChanged(nameof(Activated));

            if (PluginMonitor.Instance.RemoteWrappers.TryGetValue(_pluginDescriptor.FilePath, out var wrappers))
            {
                foreach (var wrapper in wrappers)
                {
                    var plugin = Plugins.SingleOrDefault(x => x.Id == wrapper.Id);
                    if (plugin != null)
                    {
                        plugin.Refresh();
                    }
                    else
                    {
                        Plugins.Add(new PluginModuleViewModel(wrapper, _pluginDescriptor));
                    }
                }
            }
        }
    }
}
