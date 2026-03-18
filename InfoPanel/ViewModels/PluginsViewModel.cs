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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Controls;

namespace InfoPanel.ViewModels
{
    public partial class PluginsViewModel : ObservableObject
    {
        private readonly DispatcherTimer _timer;

        [ObservableProperty]
        private string _pluginFolder = FileUtil.GetExternalPluginFolder();

        public ObservableCollection<PluginViewModel> BundledPlugins { get; } = [];

        public ObservableCollection<PluginViewModel> ExternalPlugins { get; } = [];

        public PluginsViewModel()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += OnTimerTick;

            BuildPluginModels();
        }

        public void Start()
        {
            PluginMonitor.Instance.ProcessManager.StartMetricsLoop();
            BuildPluginModels();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _ = PluginMonitor.Instance.ProcessManager.StopMetricsLoopAsync();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            BuildPluginModels();
        }

        private void BuildPluginModels()
        {
            List<PluginDescriptor> pluginsSnapshot;
            lock (PluginMonitor.Instance.PluginsLock)
            {
                pluginsSnapshot = PluginMonitor.Instance.Plugins.ToList();
            }

            var currentFilePaths = pluginsSnapshot.Select(p => p.FilePath).ToHashSet();

            // Remove stale entries (e.g. after uninstall)
            for (int i = BundledPlugins.Count - 1; i >= 0; i--)
            {
                if (!currentFilePaths.Contains(BundledPlugins[i].FilePath))
                    BundledPlugins.RemoveAt(i);
            }
            for (int i = ExternalPlugins.Count - 1; i >= 0; i--)
            {
                if (!currentFilePaths.Contains(ExternalPlugins[i].FilePath))
                    ExternalPlugins.RemoveAt(i);
            }

            foreach (var pluginDescriptor in pluginsSnapshot)
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
        public async Task AddPluginFromZip()
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new()
            {
                Multiselect = false,
                Filter = "InfoPanel Plugin Archive |InfoPanel.*.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };
            if (openFileDialog.ShowDialog() == true)
            {
                var safeName = openFileDialog.SafeFileName;

                // Accept both structured ZIPs (InfoPanel.Name/...) and flat ZIPs (filename is InfoPanel.Name.zip)
                bool isValid;
                using (var fs = new FileStream(openFileDialog.FileName, FileMode.Open))
                using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var entry = za.Entries[0];
                    isValid = Regex.IsMatch(entry.FullName, @"InfoPanel\.[a-zA-Z0-9]+\/")
                           || Regex.IsMatch(Path.GetFileNameWithoutExtension(safeName), @"^InfoPanel\.[a-zA-Z0-9]+$");
                }

                if (isValid)
                {
                    try
                    {
                        var destPath = Path.Combine(FileUtil.GetExternalPluginFolder(), safeName);
                        File.Copy(openFileDialog.FileName, destPath, true);
                        await PluginMonitor.Instance.InstallPluginFromZipAsync(destPath);
                        BuildPluginModels();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to install plugin from ZIP");
                    }
                }
            }
        }

    }

    public partial class PluginModuleViewModel : ObservableObject
    {
        private RemotePluginWrapper _wrapper;
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

            // The old wrapper is now stale — grab the new one created by StartPluginModulesAsync
            if (PluginMonitor.Instance.RemoteWrappers.TryGetValue(_pluginDescriptor.FilePath, out var wrappers))
            {
                var newWrapper = wrappers.FirstOrDefault(w => w.Id == Id);
                if (newWrapper != null)
                {
                    _wrapper = newWrapper;
                }
            }

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

        internal void Refresh(RemotePluginWrapper? newWrapper = null)
        {
            if (newWrapper != null && newWrapper != _wrapper)
            {
                _wrapper = newWrapper;
                _configLoaded = false;
            }

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

            // StreamJsonRpc deserializes JSON integers as Int64 (long) and floats as double.
            // Coerce raw CLR values to the types the UI controls expect.
            return type switch
            {
                PluginConfigType.Boolean => value is bool ? value : Convert.ToBoolean(value),
                PluginConfigType.Integer => value is int ? value : Convert.ToInt32(value),
                PluginConfigType.Double => value is double ? value : Convert.ToDouble(value),
                PluginConfigType.String => value?.ToString(),
                PluginConfigType.Choice => value?.ToString(),
                _ => value
            };
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

        public bool IsExternal { get; }

        public bool ShowRemoveButton => IsExternal && !_activated;

        private bool _activated;
        public bool Activated
        {
            get => _activated;
            set
            {
                if (SetProperty(ref _activated, value))
                {
                    OnPropertyChanged(nameof(ShowRemoveButton));
                    _ = OnActivatedChanged();
                }
            }
        }

        public ObservableCollection<PluginModuleViewModel> Plugins { get; set; } = [];

        [ObservableProperty]
        private bool _controlEnabled = true;

        [ObservableProperty]
        private double _cpuUsage;

        [ObservableProperty]
        private string _memoryUsage = "";

        [ObservableProperty]
        private bool _showMetrics;

        private static string FormatMemory(long bytes)
        {
            const double MB = 1024 * 1024;
            const double GB = 1024 * 1024 * 1024;
            if (bytes >= GB)
                return $"{bytes / GB:F1} GB";
            return $"{bytes / MB:F1} MB";
        }

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

        [RelayCommand]
        public async Task Remove()
        {
            var contentDialogService = App.GetService<Wpf.Ui.IContentDialogService>();
            if (contentDialogService != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Uninstall Plugin",
                    Content = $"Are you sure you want to uninstall \"{Name}\"? This will delete the plugin folder from disk.",
                    PrimaryButtonText = "Uninstall",
                    CloseButtonText = "Cancel"
                };

                var result = await contentDialogService.ShowAsync(dialog, CancellationToken.None);
                if (result != ContentDialogResult.Primary)
                    return;
            }

            ControlEnabled = false;
            try
            {
                await PluginMonitor.Instance.UninstallPluginAsync(_pluginDescriptor);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error uninstalling plugin {PluginName}", _pluginDescriptor.FileName);
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
            IsExternal = !(pluginDescriptor.FolderPath?.IsSubdirectoryOf(FileUtil.GetBundledPluginFolder()) ?? false);
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
            OnPropertyChanged(nameof(ShowRemoveButton));

            if (_activated)
            {
                var metrics = PluginMonitor.Instance.ProcessManager.GetProcessMetrics(_pluginDescriptor.FilePath);
                if (metrics != null)
                {
                    CpuUsage = Math.Round(metrics.CpuPercent, 1);
                    MemoryUsage = FormatMemory(metrics.MemoryBytes);
                    ShowMetrics = true;
                }
                else
                {
                    ShowMetrics = false;
                }
            }
            else
            {
                ShowMetrics = false;
            }

            if (PluginMonitor.Instance.RemoteWrappers.TryGetValue(_pluginDescriptor.FilePath, out var wrappers))
            {
                foreach (var wrapper in wrappers)
                {
                    var plugin = Plugins.SingleOrDefault(x => x.Id == wrapper.Id);
                    if (plugin != null)
                    {
                        plugin.Refresh(wrapper);
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
