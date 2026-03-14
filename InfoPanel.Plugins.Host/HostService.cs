using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using Serilog;
using StreamJsonRpc;

namespace InfoPanel.Plugins.Host
{
    public class HostService : IPluginHostService
    {
        private static readonly ILogger Logger = Log.ForContext<HostService>();
        private readonly string _pluginPath;
        private JsonRpc? _jsonRpc;
        private readonly List<PluginWrapper> _wrappers = [];
        private readonly SensorSnapshotManager _snapshotManager = new();
        private CancellationTokenSource? _updateCts;
        private Task? _updateTask;
        private Task? _perfUpdateTask;

        // CPU metrics
        private Process? _currentProcess;
        private TimeSpan _lastCpuTime;
        private DateTime _lastCheckTime = DateTime.UtcNow;

        /// <summary>
        /// Called after plugin shutdown completes to signal the host process to exit gracefully.
        /// </summary>
        public Action? OnShutdownRequested { get; set; }

        public HostService(string pluginPath)
        {
            _pluginPath = pluginPath;
        }

        public void SetJsonRpc(JsonRpc jsonRpc)
        {
            _jsonRpc = jsonRpc;
        }

        public async Task<List<PluginMetadataDto>> InitializeAsync()
        {
            Logger.Information("Initializing plugins from {PluginPath}", _pluginPath);

            var plugins = PluginLoader.InitializePlugin(_pluginPath);
            var pluginInfo = PluginLoader.GetPluginInfo(Path.GetDirectoryName(_pluginPath)!);
            var descriptor = new PluginDescriptor(_pluginPath, pluginInfo);
            var metadataList = new List<PluginMetadataDto>();
            var containersByPluginId = new Dictionary<string, List<ContainerDto>>();

            foreach (var plugin in plugins)
            {
                var wrapper = new PluginWrapper(descriptor, plugin);

                try
                {
                    await wrapper.Initialize();
                    descriptor.PluginWrappers.TryAdd(wrapper.Id, wrapper);
                    _wrappers.Add(wrapper);
                    Logger.Information("Plugin {PluginName} initialized successfully", wrapper.Name);

                    // Discover actions
                    var actions = new List<PluginActionDto>();
                    var methods = plugin.GetType().GetMethods()
                        .Where(m => m.GetCustomAttributes(typeof(PluginActionAttribute), false).Length > 0);
                    foreach (var method in methods)
                    {
                        var attribute = (PluginActionAttribute)method.GetCustomAttributes(typeof(PluginActionAttribute), false).First();
                        actions.Add(new PluginActionDto
                        {
                            MethodName = method.Name,
                            DisplayName = attribute.DisplayName
                        });
                    }

                    var metadata = new PluginMetadataDto
                    {
                        Id = wrapper.Id,
                        Name = wrapper.Name,
                        Description = wrapper.Description,
                        ConfigFilePath = wrapper.ConfigFilePath,
                        UpdateIntervalMs = wrapper.UpdateInterval.TotalMilliseconds,
                        Actions = actions,
                        IsConfigurable = plugin is IPluginConfigurable,
                        ConfigProperties = plugin is IPluginConfigurable configurable
                            ? configurable.ConfigProperties.Select(ConvertConfigPropertyToDto).ToList()
                            : []
                    };
                    metadataList.Add(metadata);

                    // Convert containers to DTOs
                    var containerDtos = new List<ContainerDto>();
                    foreach (var container in wrapper.PluginContainers)
                    {
                        var containerDto = new ContainerDto
                        {
                            Id = container.Id,
                            Name = container.Name,
                            IsEphemeralPath = container.IsEphemeralPath,
                            Entries = container.Entries.Select(ConvertEntryToDto).ToList()
                        };
                        containerDtos.Add(containerDto);
                    }
                    containersByPluginId[wrapper.Id] = containerDtos;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to initialize plugin {PluginName}", wrapper.Name);
                    NotifyError(wrapper.Id, ex.Message);
                }
            }

            // Initialize CPU baseline
            try
            {
                _currentProcess = Process.GetCurrentProcess();
                _lastCpuTime = _currentProcess.TotalProcessorTime;
                _lastCheckTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to initialize CPU baseline");
            }

            // Notify main app about loaded plugins
            NotifyPluginsLoaded(metadataList, containersByPluginId);

            // Start update loops
            _updateCts = new CancellationTokenSource();
            _updateTask = Task.Run(() => UpdateLoopAsync(_updateCts.Token));
            _perfUpdateTask = Task.Run(() => PerformanceLoopAsync(_updateCts.Token));

            return metadataList;
        }

        public Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId)
        {
            var wrapper = _wrappers.FirstOrDefault(w => w.Id == pluginId);
            if (wrapper?.Plugin is IPluginConfigurable configurable)
            {
                return Task.FromResult(configurable.ConfigProperties.Select(ConvertConfigPropertyToDto).ToList());
            }
            return Task.FromResult(new List<PluginConfigPropertyDto>());
        }

        public Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value)
        {
            Logger.Information("ApplyConfigAsync called: pluginId={PluginId}, key={Key}, valueType={ValueType}, value={Value}",
                pluginId, key, value?.GetType().Name ?? "null", value);

            var wrapper = _wrappers.FirstOrDefault(w => w.Id == pluginId);
            if (wrapper?.Plugin is IPluginConfigurable configurable)
            {
                // Find the property type so we can coerce the JSON-RPC value to the correct CLR type.
                // Values arrive as JToken over StreamJsonRpc and must be converted before
                // passing to the plugin's ApplyConfig.
                var prop = configurable.ConfigProperties.FirstOrDefault(p => p.Key == key);
                var nativeValue = prop != null ? CoerceValue(value, prop.Type) : value;

                Logger.Information("Config coercion: key={Key}, rawType={RawType}, nativeType={NativeType}, nativeValue={NativeValue}",
                    key, value?.GetType().Name ?? "null", nativeValue?.GetType().Name ?? "null", nativeValue);

                try
                {
                    configurable.ApplyConfig(key, nativeValue);
                    Logger.Information("Config applied successfully for key={Key}", key);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error applying config key {Key} on plugin {PluginId}", key, pluginId);
                    NotifyError(pluginId, $"Config '{key}' apply failed: {ex.Message}");
                }
                // Return refreshed properties (ApplyConfig may change other properties)
                return Task.FromResult(configurable.ConfigProperties.Select(ConvertConfigPropertyToDto).ToList());
            }

            Logger.Warning("ApplyConfigAsync: plugin {PluginId} not found or not configurable", pluginId);
            return Task.FromResult(new List<PluginConfigPropertyDto>());
        }

        private static object? CoerceValue(object? value, PluginConfigType type)
        {
            if (value is JToken jt)
            {
                try
                {
                    return type switch
                    {
                        PluginConfigType.Boolean => jt.Type == JTokenType.Boolean ? jt.Value<bool>()
                                                  : bool.TryParse(jt.ToString(), out var b) ? b : false,
                        PluginConfigType.Integer => jt.Type == JTokenType.Integer ? jt.Value<int>()
                                                  : jt.Type == JTokenType.Float ? (int)Math.Round(jt.Value<double>())
                                                  : int.TryParse(jt.ToString(), out var i) ? i : 0,
                        PluginConfigType.Double => jt.Type == JTokenType.Float || jt.Type == JTokenType.Integer
                                                  ? jt.Value<double>()
                                                  : double.TryParse(jt.ToString(), System.Globalization.NumberStyles.Float,
                                                      System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0,
                        PluginConfigType.String => jt.ToString(),
                        PluginConfigType.Choice => jt.ToString(),
                        _ => jt.ToString()
                    };
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to coerce JToken value {Value} to {Type}, returning default", jt, type);
                    return type switch
                    {
                        PluginConfigType.Boolean => false,
                        PluginConfigType.Integer => 0,
                        PluginConfigType.Double => 0.0,
                        _ => jt.ToString()
                    };
                }
            }

            // StreamJsonRpc deserializes JSON integers as Int64 (long) and floats as double.
            // Coerce raw CLR values to the types plugins expect.
            try
            {
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
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to coerce CLR value {Value} to {Type}, returning default", value, type);
                return type switch
                {
                    PluginConfigType.Boolean => false,
                    PluginConfigType.Integer => 0,
                    PluginConfigType.Double => 0.0,
                    PluginConfigType.String => value?.ToString(),
                    PluginConfigType.Choice => value?.ToString(),
                    _ => value
                };
            }
        }

        public Task InvokeActionAsync(string pluginId, string methodName)
        {
            var wrapper = _wrappers.FirstOrDefault(w => w.Id == pluginId);
            if (wrapper == null)
            {
                Logger.Warning("Plugin {PluginId} not found for action {MethodName}", pluginId, methodName);
                return Task.CompletedTask;
            }

            try
            {
                var method = wrapper.Plugin.GetType().GetMethod(methodName, Type.EmptyTypes);
                if (method == null)
                {
                    Logger.Warning("Action method {MethodName} not found on plugin {PluginId}", methodName, pluginId);
                    return Task.CompletedTask;
                }
                method.Invoke(wrapper.Plugin, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                Logger.Error(ex.InnerException, "Error invoking action {MethodName} on plugin {PluginId}", methodName, pluginId);
                NotifyError(pluginId, $"Action '{methodName}' failed: {ex.InnerException.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error invoking action {MethodName} on plugin {PluginId}", methodName, pluginId);
                NotifyError(pluginId, $"Action '{methodName}' failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public async Task ShutdownAsync()
        {
            Logger.Information("Shutdown requested");

            if (_updateCts != null)
            {
                _updateCts.Cancel();
                if (_updateTask != null)
                {
                    try { await _updateTask; } catch (OperationCanceledException) { }
                }
                if (_perfUpdateTask != null)
                {
                    try { await _perfUpdateTask; } catch (OperationCanceledException) { }
                }
                _updateCts.Dispose();
            }

            foreach (var wrapper in _wrappers)
            {
                try
                {
                    await wrapper.StopAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error stopping plugin {PluginName}", wrapper.Name);
                }
            }

            // Signal the host process to exit gracefully by disposing the JsonRpc connection.
            // This causes jsonRpc.Completion in Program.cs to complete naturally.
            OnShutdownRequested?.Invoke();
        }

        private async Task UpdateLoopAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var batches = new List<SensorUpdateBatchDto>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Call Update() on sync plugins (UpdateInterval <= 0)
                    foreach (var wrapper in _wrappers)
                    {
                        if (wrapper.IsLoaded)
                        {
                            try
                            {
                                wrapper.Update();
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning(ex, "Error updating plugin {PluginName}", wrapper.Name);
                            }
                        }
                    }

                    // Snapshot and push changed values
                    batches.Clear();

                    foreach (var wrapper in _wrappers)
                    {
                        if (!wrapper.IsLoaded) continue;

                        var updates = _snapshotManager.GetChangedEntries(wrapper);
                        if (updates.Count > 0)
                        {
                            batches.Add(new SensorUpdateBatchDto
                            {
                                PluginId = wrapper.Id,
                                Updates = updates
                            });
                        }
                    }

                    if (batches.Count > 0)
                    {
                        NotifySensorUpdate(batches);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex, "Error in update loop");
                }

                await Task.Delay(100, token);
            }
        }

        private async Task PerformanceLoopAsync(CancellationToken token)
        {
            await Task.Delay(2000, token);

            var performances = new List<PluginPerformanceDto>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var cpuPercent = GetCpuPercent();
                    performances.Clear();

                    foreach (var wrapper in _wrappers)
                    {
                        if (!wrapper.IsLoaded) continue;

                        performances.Add(new PluginPerformanceDto
                        {
                            PluginId = wrapper.Id,
                            UpdateTimeMilliseconds = wrapper.UpdateTimeMilliseconds,
                            CpuPercent = cpuPercent
                        });
                    }

                    if (performances.Count > 0)
                    {
                        NotifyPerformanceUpdate(performances);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex, "Error in performance loop");
                }

                await Task.Delay(2000, token);
            }
        }

        private double GetCpuPercent()
        {
            var proc = _currentProcess;
            if (proc == null) return 0;

            try
            {
                proc.Refresh();
                var now = DateTime.UtcNow;
                var cpuTime = proc.TotalProcessorTime;
                var elapsed = now - _lastCheckTime;

                if (elapsed.TotalMilliseconds > 0)
                {
                    var cpuDelta = (cpuTime - _lastCpuTime).TotalMilliseconds;
                    var cpuPercent = cpuDelta / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;

                    _lastCpuTime = cpuTime;
                    _lastCheckTime = now;

                    return Math.Clamp(cpuPercent, 0, 100);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to compute CPU metrics");
            }

            return 0;
        }

        private EntryDto ConvertEntryToDto(IPluginData entry)
        {
            var dto = new EntryDto
            {
                Id = entry.Id,
                Name = entry.Name
            };

            if (entry is IPluginSensor sensor)
            {
                dto.Type = "sensor";
                dto.SensorValue = new SensorValueDto
                {
                    Value = sensor.Value,
                    ValueMin = sensor.ValueMin,
                    ValueMax = sensor.ValueMax,
                    ValueAvg = sensor.ValueAvg,
                    Unit = sensor.Unit
                };
            }
            else if (entry is IPluginText text)
            {
                dto.Type = "text";
                dto.TextValue = text.Value;
            }
            else if (entry is IPluginTable table)
            {
                dto.Type = "table";
                dto.TableValue = TableDtoConverter.ConvertTableToDto(table);
            }

            return dto;
        }

        private static PluginConfigPropertyDto ConvertConfigPropertyToDto(PluginConfigProperty prop)
        {
            return new PluginConfigPropertyDto
            {
                Key = prop.Key,
                DisplayName = prop.DisplayName,
                Description = prop.Description,
                Type = prop.Type.ToString(),
                Value = prop.Value,
                MinValue = prop.MinValue,
                MaxValue = prop.MaxValue,
                Step = prop.Step,
                Options = prop.Options
            };
        }

        private void NotifyPluginsLoaded(List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containers)
        {
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnPluginsLoaded), plugins, containers);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify plugins loaded");
            }
        }

        private void NotifySensorUpdate(List<SensorUpdateBatchDto> updates)
        {
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnSensorUpdate), updates);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify sensor update");
            }
        }

        private void NotifyError(string pluginId, string message)
        {
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnPluginError), pluginId, message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify plugin error");
            }
        }

        private void NotifyPerformanceUpdate(List<PluginPerformanceDto> performances)
        {
            try
            {
                _jsonRpc?.NotifyAsync(nameof(IPluginClientCallback.OnPerformanceUpdate), performances);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to notify performance update");
            }
        }
    }
}
