using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Extensions;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Serilog;

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
        private PluginWrapper _wrapper;
        public string Id { get; set; }

        [ObservableProperty]
        private string _name;
        [ObservableProperty]
        private string _description;
        [ObservableProperty]
        private string? _configFilePath;

        public ObservableCollection<PluginActionCommand> Actions { get; } = [];
        public ObservableCollection<PluginConfigPropertyViewModel> ConfigProperties { get; } = [];

        [ObservableProperty]
        private bool _hasConfigProperties;

        [RelayCommand]
        public async Task Reload()
        {
            await PluginMonitor.Instance.ReloadPluginModule(_wrapper);
            RefreshConfigProperties();
        }

        public PluginModuleViewModel(PluginWrapper wrapper)
        {
            _wrapper = wrapper;
            Id = wrapper.Id;
            Name = wrapper.Name;
            Description = wrapper.Description;
            ConfigFilePath = wrapper.ConfigFilePath;

            var methods = wrapper.Plugin.GetType().GetMethods().Where(m => m.GetCustomAttributes(typeof(PluginActionAttribute), false).Length > 0);

            foreach (var method in methods)
            {
                var attribute = (PluginActionAttribute)method.GetCustomAttributes(typeof(PluginActionAttribute), false).First();
                string displayName = attribute.DisplayName;

                var command = new RelayCommand(() => method.Invoke(wrapper.Plugin, null));
                Actions.Add(new PluginActionCommand { DisplayName = displayName, Command = command });
            }

            RefreshConfigProperties();
        }

        private void RefreshConfigProperties()
        {
            if (_wrapper.Plugin is IPluginConfigurable configurable)
            {
                var properties = configurable.ConfigProperties;
                ConfigProperties.Clear();
                foreach (var prop in properties)
                {
                    ConfigProperties.Add(new PluginConfigPropertyViewModel(configurable, prop));
                }
                foreach (var vm in ConfigProperties)
                {
                    vm.SetSiblings(ConfigProperties);
                }
                HasConfigProperties = ConfigProperties.Count > 0;
            }
            else
            {
                HasConfigProperties = false;
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

    public partial class PluginConfigPropertyViewModel : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<PluginConfigPropertyViewModel>();

        private readonly IPluginConfigurable _configurable;
        private readonly PluginConfigProperty _property;

        public string Key => _property.Key;
        public string DisplayName => _property.DisplayName;
        public string? Description => _property.Description;
        public PluginConfigType Type => _property.Type;
        public double? MinValue => _property.MinValue;
        public double? MaxValue => _property.MaxValue;
        public double? Step => _property.Step;
        public string[]? Options => _property.Options;

        [ObservableProperty]
        private object? _value;

        public bool BoolValue
        {
            get => Value is true;
            set { Value = value; OnPropertyChanged(); Apply(); }
        }

        public string StringValue
        {
            get => Value?.ToString() ?? "";
            set { Value = value; OnPropertyChanged(); Apply(); }
        }

        public double NumericValue
        {
            get => Convert.ToDouble(Value ?? 0.0);
            set
            {
                if (Type == PluginConfigType.Integer)
                {
                    var i = (int)Math.Round(value);
                    if (MinValue.HasValue && MaxValue.HasValue)
                        i = Math.Clamp(i, (int)Math.Ceiling(MinValue.Value), (int)Math.Floor(MaxValue.Value));
                    else if (MinValue.HasValue)
                        i = Math.Max((int)Math.Ceiling(MinValue.Value), i);
                    else if (MaxValue.HasValue)
                        i = Math.Min((int)Math.Floor(MaxValue.Value), i);
                    Value = i;
                }
                else
                {
                    var d = value;
                    if (MinValue.HasValue && MaxValue.HasValue)
                        d = Math.Clamp(d, MinValue.Value, MaxValue.Value);
                    else if (MinValue.HasValue)
                        d = Math.Max(MinValue.Value, d);
                    else if (MaxValue.HasValue)
                        d = Math.Min(MaxValue.Value, d);
                    Value = d;
                }
                OnPropertyChanged();
                Apply();
            }
        }

        public int SelectedIndex
        {
            get
            {
                if (Options == null || Value == null) return -1;
                return Array.IndexOf(Options, Value.ToString());
            }
            set
            {
                if (Options != null && value >= 0 && value < Options.Length)
                {
                    Value = Options[value];
                    OnPropertyChanged();
                    Apply();
                }
            }
        }

        public PluginConfigPropertyViewModel(IPluginConfigurable configurable, PluginConfigProperty property)
        {
            _configurable = configurable;
            _property = property;
            _value = property.Value;
        }

        [RelayCommand]
        private void Apply()
        {
            try
            {
                object? typedValue;
                switch (Type)
                {
                    case PluginConfigType.Integer:
                        if (int.TryParse(Value?.ToString(), out var i))
                        {
                            if (MinValue.HasValue && MaxValue.HasValue)
                                i = Math.Clamp(i, (int)Math.Ceiling(MinValue.Value), (int)Math.Floor(MaxValue.Value));
                            else if (MinValue.HasValue)
                                i = Math.Max((int)Math.Ceiling(MinValue.Value), i);
                            else if (MaxValue.HasValue)
                                i = Math.Min((int)Math.Floor(MaxValue.Value), i);
                            typedValue = i;
                        }
                        else
                        {
                            Logger.Warning("Plugin config '{ConfigKey}': could not parse '{RawValue}' as integer", Key, Value);
                            return;
                        }
                        break;
                    case PluginConfigType.Double:
                        if (double.TryParse(Value?.ToString(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            if (MinValue.HasValue && MaxValue.HasValue)
                                d = Math.Clamp(d, MinValue.Value, MaxValue.Value);
                            else if (MinValue.HasValue)
                                d = Math.Max(MinValue.Value, d);
                            else if (MaxValue.HasValue)
                                d = Math.Min(MaxValue.Value, d);
                            typedValue = d;
                        }
                        else
                        {
                            Logger.Warning("Plugin config '{ConfigKey}': could not parse '{RawValue}' as double", Key, Value);
                            return;
                        }
                        break;
                    default:
                        typedValue = Value;
                        break;
                }

                _configurable.ApplyConfig(Key, typedValue);

                // Re-read source properties to pick up cross-property changes (e.g. mutual exclusion)
                foreach (var sourceProp in _configurable.ConfigProperties)
                {
                    if (sourceProp.Key != Key && _siblings != null)
                    {
                        var sibling = _siblings.FirstOrDefault(vm => vm.Key == sourceProp.Key);
                        if (sibling != null && !Equals(sibling.Value, sourceProp.Value))
                        {
                            sibling.RefreshValue(sourceProp.Value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Plugin config '{ConfigKey}': ApplyConfig failed", Key);
            }
        }

        private IEnumerable<PluginConfigPropertyViewModel>? _siblings;
        internal void SetSiblings(IEnumerable<PluginConfigPropertyViewModel> siblings) => _siblings = siblings;

        public void RefreshValue(object? newValue)
        {
            Value = newValue;
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(StringValue));
            OnPropertyChanged(nameof(NumericValue));
            OnPropertyChanged(nameof(SelectedIndex));
        }
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
            _activated = pluginDescriptor.PluginWrappers.Any(x => x.Value.IsRunning);

            foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
            {
                Plugins.Add(new PluginModuleViewModel(wrapper));
            }
        }

        public void Refresh()
        {
            if (!ControlEnabled) { return; }

            _activated = _pluginDescriptor.PluginWrappers.Any(x => x.Value.IsRunning);
            OnPropertyChanged(nameof(Activated));

            foreach (var wrapper in _pluginDescriptor.PluginWrappers.Values)
            {
                var plugin = Plugins.SingleOrDefault(x => x.Id == wrapper.Id);
                if (plugin != null)
                {
                    plugin.Refresh();
                }
                else
                {
                    Plugins.Add(new PluginModuleViewModel(wrapper));
                }
            }
        }
    }
}
