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
                descriptor.PluginWrappers.TryAdd(wrapper.Id, wrapper);
                _wrappers.Add(wrapper);

                try
                {
                    await wrapper.Initialize();
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

            // Notify main app about loaded plugins
            NotifyPluginsLoaded(metadataList, containersByPluginId);

            // Start update loops
            _updateCts = new CancellationTokenSource();
            _updateTask = Task.Run(() => UpdateLoopAsync(_updateCts.Token));

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
            // Coerce raw CLR values to the types plugins expect.
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
                var method = wrapper.Plugin.GetType().GetMethod(methodName);
                method?.Invoke(wrapper.Plugin, null);
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

            // Give time for the shutdown response to be sent before exiting
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Environment.Exit(0);
            });
        }

        private async Task UpdateLoopAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

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
                            catch { }
                        }
                    }

                    // Snapshot and push changed values
                    var batches = new List<SensorUpdateBatchDto>();
                    var performances = new List<PluginPerformanceDto>();

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

                        performances.Add(new PluginPerformanceDto
                        {
                            PluginId = wrapper.Id,
                            UpdateTimeMilliseconds = wrapper.UpdateTimeMilliseconds
                        });
                    }

                    if (batches.Count > 0)
                    {
                        NotifySensorUpdate(batches);
                    }

                    if (performances.Count > 0)
                    {
                        NotifyPerformanceUpdate(performances);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex, "Error in update loop");
                }

                await Task.Delay(100, token);
            }
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
                dto.TableValue = ConvertTableToDto(table);
            }

            return dto;
        }

        private static TableValueDto ConvertTableToDto(IPluginTable table)
        {
            var dto = new TableValueDto
            {
                DefaultFormat = table.DefaultFormat,
                Columns = [],
                Rows = []
            };

            var dt = table.Value;
            foreach (System.Data.DataColumn col in dt.Columns)
            {
                dto.Columns.Add(col.ColumnName);
            }

            foreach (System.Data.DataRow row in dt.Rows)
            {
                var rowCells = new List<TableCellDto>();
                for (int i = 0; i < dt.Columns.Count; i++)
                {
                    var cell = new TableCellDto();
                    var value = row[i];
                    if (value is IPluginSensor cellSensor)
                    {
                        cell.Type = "sensor";
                        cell.SensorName = cellSensor.Name;
                        cell.SensorUnit = cellSensor.Unit;
                        cell.SensorValue = new SensorValueDto
                        {
                            Value = cellSensor.Value,
                            ValueMin = cellSensor.ValueMin,
                            ValueMax = cellSensor.ValueMax,
                            ValueAvg = cellSensor.ValueAvg,
                            Unit = cellSensor.Unit
                        };
                    }
                    else if (value is IPluginText cellText)
                    {
                        cell.Type = "text";
                        cell.TextValue = cellText.Value;
                    }
                    else
                    {
                        cell.Type = "text";
                        cell.TextValue = value?.ToString() ?? "";
                    }
                    rowCells.Add(cell);
                }
                dto.Rows.Add(rowCells);
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
