using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Extensions;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using Serilog;
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
using Newtonsoft.Json.Linq;

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
        public ObservableCollection<PluginConfigPropertyViewModel> ConfigProperties { get; } = [];

        [ObservableProperty]
        private bool _hasConfigProperties;

        [ObservableProperty]
        private bool _configLoaded = false;

        [RelayCommand]
        public async Task Reload()
        {
            await PluginMonitor.Instance.ReloadPluginModule(_pluginDescriptor);
            RefreshConfigProperties();
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

            RefreshConfigProperties();
        }

        private void RefreshConfigProperties()
        {
            if (!_wrapper.IsLoaded || !_wrapper.IsConfigurable)
            {
                ConfigProperties.Clear();
                HasConfigProperties = false;
                ConfigLoaded = false;
                return;
            }

            var dtos = _wrapper.ConfigProperties;
            ConfigProperties.Clear();
            foreach (var dto in dtos)
            {
                ConfigProperties.Add(new PluginConfigPropertyViewModel(_wrapper, dto));
            }
            foreach (var vm in ConfigProperties)
            {
                vm.SetSiblings(ConfigProperties);
            }
            HasConfigProperties = ConfigProperties.Count > 0;
            ConfigLoaded = HasConfigProperties;
        }

        public void Refresh()
        {
            Id = _wrapper.Id;
            Name = _wrapper.Name;
            Description = _wrapper.Description;
            ConfigFilePath = _wrapper.ConfigFilePath;

            if (!_configLoaded)
            {
                RefreshConfigProperties();
            }
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

        private readonly RemotePluginWrapper _wrapper;
        private readonly PluginConfigPropertyDto _property;

        public string Key => _property.Key;
        public string DisplayName => _property.DisplayName;
        public string? Description => _property.Description;
        public PluginConfigType Type { get; }
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

        internal PluginConfigPropertyViewModel(RemotePluginWrapper wrapper, PluginConfigPropertyDto property)
        {
            _wrapper = wrapper;
            _property = property;
            Type = Enum.TryParse<PluginConfigType>(property.Type, out var t) ? t : PluginConfigType.String;
            _value = CoerceFromDto(property.Value, Type);
        }

        [RelayCommand]
        private void Apply()
        {
            _ = ApplyAsync();
        }

        private async Task ApplyAsync()
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
                        double d;
                        switch (Value)
                        {
                            case double directDouble:
                                d = directDouble;
                                break;
                            case float floatValue:
                                d = floatValue;
                                break;
                            case int intValue:
                                d = intValue;
                                break;
                            case long longValue:
                                d = longValue;
                                break;
                            case string stringValue when double.TryParse(
                                stringValue,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var parsedFromString):
                                d = parsedFromString;
                                break;
                            default:
                                if (!double.TryParse(
                                    Value?.ToString() ?? "",
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var parsedFromOther))
                                {
                                    Logger.Warning("Plugin config '{ConfigKey}': could not parse '{RawValue}' as double", Key, Value);
                                    return;
                                }
                                d = parsedFromOther;
                                break;
                        }
                        if (MinValue.HasValue && MaxValue.HasValue)
                            d = Math.Clamp(d, MinValue.Value, MaxValue.Value);
                        else if (MinValue.HasValue)
                            d = Math.Max(MinValue.Value, d);
                        else if (MaxValue.HasValue)
                            d = Math.Min(MaxValue.Value, d);
                        typedValue = d;
                        break;
                    default:
                        typedValue = Value;
                        break;
                }

                // Send config change to host process via IPC
                var updatedProperties = await _wrapper.ApplyConfigAsync(Key, typedValue);

                // Re-read source properties to pick up cross-property changes (e.g. mutual exclusion)
                if (_siblings != null)
                {
                    foreach (var updatedProp in updatedProperties)
                    {
                        if (updatedProp.Key != Key)
                        {
                            var sibling = _siblings.FirstOrDefault(vm => vm.Key == updatedProp.Key);
                            if (sibling != null)
                            {
                                var coerced = CoerceFromDto(updatedProp.Value, sibling.Type);
                                if (!Equals(sibling.Value, coerced))
                                {
                                    sibling.RefreshValue(coerced);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Plugin config '{ConfigKey}': ApplyConfig IPC failed (type={ExType})", Key, ex.GetType().Name);
            }
        }

        private IEnumerable<PluginConfigPropertyViewModel>? _siblings;
        internal void SetSiblings(IEnumerable<PluginConfigPropertyViewModel> siblings) => _siblings = siblings;

        public void RefreshValue(object? newValue)
        {
            Value = CoerceFromDto(newValue, Type);
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(StringValue));
            OnPropertyChanged(nameof(NumericValue));
            OnPropertyChanged(nameof(SelectedIndex));
        }

        private static object? CoerceFromDto(object? value, PluginConfigType type)
        {
            if (value is JToken jt)
            {
                return type switch
                {
                    PluginConfigType.Boolean => jt.Type == JTokenType.Boolean ? jt.Value<bool>() : value,
                    PluginConfigType.Integer => jt.Type == JTokenType.Integer ? jt.Value<int>()
                                              : jt.Type == JTokenType.Float ? (int)Math.Round(jt.Value<double>())
                                              : int.TryParse(jt.ToString(), out var i) ? i : value,
                    PluginConfigType.Double => jt.Type == JTokenType.Float || jt.Type == JTokenType.Integer
                                              ? jt.Value<double>()
                                              : double.TryParse(jt.ToString(), System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : value,
                    PluginConfigType.String => jt.ToString(),
                    PluginConfigType.Choice => jt.ToString(),
                    _ => value
                };
            }
            return value;
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
            try
            {
                if (!_activated)
                {
                    await PluginMonitor.Instance.StopPluginModulesAsync(_pluginDescriptor);
                }
                else
                {
                    await PluginMonitor.Instance.StartPluginModulesAsync(_pluginDescriptor);
                }

                PluginMonitor.Instance.SavePluginState();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error toggling plugin {PluginName}", _pluginDescriptor.FileName);
            }
            finally
            {
                ControlEnabled = true;
            }
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
