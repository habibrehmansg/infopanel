using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Monitors.PluginProxies;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Utils;
using Serilog;
using StreamJsonRpc;

namespace InfoPanel.Monitors
{
    internal record ProcessMetrics(double CpuPercent, long MemoryBytes);

    /// <summary>
    /// Manages a single host process and its JSON-RPC connection.
    /// </summary>
    internal class PluginHostConnection : IPluginClientCallback, IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<PluginHostConnection>();

        private readonly string _pluginDllPath;
        private readonly string _pipeName;
        private readonly JobObject? _jobObject;

        private Process? _hostProcess;
        private NamedPipeServerStream? _pipeServer;
        private JsonRpc? _jsonRpc;

        private List<PluginMetadataDto>? _pluginMetadata;
        private Dictionary<string, List<ContainerDto>>? _containersByPluginId;

        // Proxy data indexed by plugin ID -> container ID -> entry ID
        private readonly ConcurrentDictionary<string, Dictionary<string, ProxyPluginContainer>> _proxyContainers = new();
        private readonly ConcurrentDictionary<string, Dictionary<string, IPluginData>> _proxyEntries = new();
        private readonly ConcurrentDictionary<string, List<ProxyPluginImage>> _proxyImages = new();
        private readonly ConcurrentDictionary<string, long> _performanceData = new();
        private volatile PerformanceCounter? _privateWorkingSetCounter;
        private volatile ProcessMetrics? _cachedMetrics;
        private double _pushedCpuPercent;

        private readonly TaskCompletionSource _pluginsLoadedTcs = new();
        private volatile bool _intentionalStop;
        private int _disconnectedRaised;

        public bool IsConnected => _jsonRpc != null && !_jsonRpc.IsDisposed;
        public bool IsProcessRunning => _hostProcess != null && !_hostProcess.HasExited;
        public List<PluginMetadataDto>? PluginMetadata => _pluginMetadata;

        public event Action? OnDisconnected;
        public event Action<string, string>? OnError;

        public PluginHostConnection(string pluginDllPath, JobObject? jobObject = null)
        {
            _pluginDllPath = pluginDllPath;
            _pipeName = $"InfoPanel.Plugin.{Guid.NewGuid():N}";
            _jobObject = jobObject;
        }

        public async Task<List<PluginMetadataDto>> StartAsync(CancellationToken token = default)
        {
            _pipeServer = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var hostExePath = GetHostExePath();
            if (!File.Exists(hostExePath))
            {
                throw new FileNotFoundException($"Plugin host executable not found: {hostExePath}");
            }

            var arguments = $"--plugin-host --pipe \"{_pipeName}\" --plugin \"{_pluginDllPath}\"";

            Logger.Information("Starting plugin host for {PluginPath} on pipe {PipeName}", _pluginDllPath, _pipeName);

            if (_jobObject != null)
            {
                _hostProcess = _jobObject.CreateChildProcess(hostExePath, arguments);
            }
            else
            {
                _hostProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = hostExePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    },
                };
                _hostProcess.Start();
            }

            _hostProcess.EnableRaisingEvents = true;
            _hostProcess.Exited += (_, _) =>
            {
                Logger.Warning("Plugin host process exited for {PluginPath}", _pluginDllPath);
                RaiseDisconnectedOnce();
            };

            // Wait for host to connect
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await _pipeServer.WaitForConnectionAsync(connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Error("Plugin host connection timed out for {PluginPath}", _pluginDllPath);
                await KillProcessAsync();
                throw new TimeoutException($"Plugin host failed to connect within 30 seconds: {_pluginDllPath}");
            }

            Logger.Information("Plugin host connected for {PluginPath}", _pluginDllPath);

            _jsonRpc = new JsonRpc(_pipeServer);
            _jsonRpc.AddLocalRpcTarget<IPluginClientCallback>(this, new JsonRpcTargetOptions { NotifyClientOfEvents = false });
            _jsonRpc.Disconnected += (_, _) =>
            {
                Logger.Warning("JSON-RPC disconnected for {PluginPath}", _pluginDllPath);
                RaiseDisconnectedOnce();
            };
            _jsonRpc.StartListening();

            // Call InitializeAsync on the host
            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            initCts.CancelAfter(TimeSpan.FromSeconds(30));

            var metadata = await _jsonRpc.InvokeWithCancellationAsync<List<PluginMetadataDto>>(
                nameof(IPluginHostService.InitializeAsync),
                cancellationToken: initCts.Token);

            _pluginMetadata = metadata;

            // Wait for OnPluginsLoaded callback
            using var loadedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            loadedCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _pluginsLoadedTcs.Task.WaitAsync(loadedCts.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Warning("OnPluginsLoaded callback timed out for {PluginPath}, using metadata from InitializeAsync", _pluginDllPath);
            }

            return metadata;
        }

        public async Task InvokeActionAsync(string pluginId, string methodName)
        {
            if (_jsonRpc == null || _jsonRpc.IsDisposed)
            {
                Logger.Warning("Cannot invoke action, not connected");
                return;
            }

            await _jsonRpc.InvokeAsync(nameof(IPluginHostService.InvokeActionAsync), pluginId, methodName);
        }

        public async Task<List<PluginConfigPropertyDto>> GetConfigPropertiesAsync(string pluginId)
        {
            if (_jsonRpc == null || _jsonRpc.IsDisposed) return [];
            return await _jsonRpc.InvokeAsync<List<PluginConfigPropertyDto>>(
                nameof(IPluginHostService.GetConfigPropertiesAsync), pluginId);
        }

        public async Task<List<PluginConfigPropertyDto>> ApplyConfigAsync(string pluginId, string key, object? value)
        {
            if (_jsonRpc == null || _jsonRpc.IsDisposed) return [];
            return await _jsonRpc.InvokeAsync<List<PluginConfigPropertyDto>>(
                nameof(IPluginHostService.ApplyConfigAsync), pluginId, key, value);
        }

        private void RaiseDisconnectedOnce()
        {
            if (_intentionalStop) return;
            if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 0)
            {
                OnDisconnected?.Invoke();
            }
        }

        public async Task StopAsync()
        {
            _intentionalStop = true;
            if (_jsonRpc != null && !_jsonRpc.IsDisposed)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _jsonRpc.InvokeWithCancellationAsync(
                        nameof(IPluginHostService.ShutdownAsync),
                        cancellationToken: cts.Token);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Error during graceful shutdown for {PluginPath}", _pluginDllPath);
                }
            }

            await KillProcessAsync();
        }

        public List<ProxyPluginContainer> GetProxyContainers(string pluginId)
        {
            if (_proxyContainers.TryGetValue(pluginId, out var containers))
                return [.. containers.Values];
            return [];
        }

        public List<ProxyPluginImage> GetProxyImages(string pluginId)
        {
            if (_proxyImages.TryGetValue(pluginId, out var images))
                return images;
            return [];
        }

        public List<ProxyPluginImage> GetAllProxyImages()
        {
            var result = new List<ProxyPluginImage>();
            foreach (var images in _proxyImages.Values)
                result.AddRange(images);
            return result;
        }

        /// <summary>
        /// Invalidates all plugin image cache entries for this connection.
        /// Must be called before disposing so new entries can be created on reconnect.
        /// </summary>
        public void InvalidateImageCaches()
        {
            foreach (var kvp in _proxyImages)
            {
                var pluginId = kvp.Key;
                var imageIds = kvp.Value.Select(img => img.ImageId);
                Cache.InvalidatePluginImages(pluginId, imageIds);
            }
        }

        public bool HasMetricsCounter => _privateWorkingSetCounter != null;

        public ProcessMetrics? ProcessMetrics => _cachedMetrics;

        /// <summary>
        /// Reads the performance counter and caches the result.
        /// Called periodically from the metrics background loop.
        /// </summary>
        internal void UpdateCachedMetrics()
        {
            var counter = _privateWorkingSetCounter;
            if (counter == null)
            {
                _cachedMetrics = null;
                return;
            }

            try
            {
                _cachedMetrics = new ProcessMetrics(_pushedCpuPercent, counter.RawValue);
            }
            catch
            {
                _privateWorkingSetCounter = null;
                _cachedMetrics = null;
                counter.Dispose();
            }
        }

        public int GetProcessId()
        {
            try { return _hostProcess?.Id ?? 0; }
            catch { return 0; }
        }

        public string? GetProcessName()
        {
            try { return _hostProcess?.ProcessName; }
            catch { return null; }
        }

        internal void SetPrivateWorkingSetCounter(PerformanceCounter? counter)
        {
            var old = Interlocked.Exchange(ref _privateWorkingSetCounter, counter);
            old?.Dispose();
        }

        public long GetUpdateTimeMilliseconds(string pluginId)
        {
            return _performanceData.GetValueOrDefault(pluginId, 0);
        }

        #region IPluginClientCallback

        public void OnPluginsLoaded(List<PluginMetadataDto> plugins, Dictionary<string, List<ContainerDto>> containersByPluginId)
        {
            Logger.Information("OnPluginsLoaded received: {Count} plugins", plugins.Count);
            _pluginMetadata = plugins;
            _containersByPluginId = containersByPluginId;

            // Build proxy containers and entries
            foreach (var kvp in containersByPluginId)
            {
                var pluginId = kvp.Key;
                var containerDtos = kvp.Value;
                var containers = new Dictionary<string, ProxyPluginContainer>();
                var entries = new Dictionary<string, IPluginData>();

                foreach (var containerDto in containerDtos)
                {
                    var proxyContainer = new ProxyPluginContainer(containerDto.Id, containerDto.Name, containerDto.IsEphemeralPath);

                    foreach (var entryDto in containerDto.Entries)
                    {
                        IPluginData proxyEntry = entryDto.Type switch
                        {
                            "sensor" => CreateProxySensor(entryDto),
                            "text" => CreateProxyText(entryDto),
                            "table" => CreateProxyTable(entryDto),
                            _ => CreateProxyText(entryDto)
                        };

                        proxyContainer.Entries.Add(proxyEntry);
                        entries[$"{containerDto.Id}/{entryDto.Id}"] = proxyEntry;
                    }

                    containers[containerDto.Id] = proxyContainer;
                }

                _proxyContainers[pluginId] = containers;
                _proxyEntries[pluginId] = entries;
            }

            // Build proxy images from metadata
            foreach (var pluginMeta in plugins)
            {
                if (pluginMeta.ImageDescriptors.Count > 0)
                {
                    var images = new List<ProxyPluginImage>();
                    foreach (var imgDesc in pluginMeta.ImageDescriptors)
                    {
                        try
                        {
                            var proxyImage = new ProxyPluginImage(pluginMeta.Id, imgDesc);
                            images.Add(proxyImage);
                            Logger.Information("Created proxy image for {PluginId}/{ImageId}", pluginMeta.Id, imgDesc.Id);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to create proxy image for {PluginId}/{ImageId}", pluginMeta.Id, imgDesc.Id);
                        }
                    }
                    _proxyImages[pluginMeta.Id] = images;
                }
            }

            _pluginsLoadedTcs.TrySetResult();
        }

        public void OnSensorUpdate(List<SensorUpdateBatchDto> updates)
        {
            foreach (var batch in updates)
            {
                if (!_proxyEntries.TryGetValue(batch.PluginId, out var entries))
                    continue;

                foreach (var update in batch.Updates)
                {
                    var key = $"{update.ContainerId}/{update.EntryId}";
                    if (!entries.TryGetValue(key, out var entry))
                        continue;

                    switch (update.Type)
                    {
                        case "sensor" when entry is ProxyPluginSensor sensor && update.SensorValue != null:
                            sensor.Value = update.SensorValue.Value;
                            sensor.ValueMin = update.SensorValue.ValueMin;
                            sensor.ValueMax = update.SensorValue.ValueMax;
                            sensor.ValueAvg = update.SensorValue.ValueAvg;
                            break;
                        case "text" when entry is ProxyPluginText text && update.TextValue != null:
                            text.Value = update.TextValue;
                            break;
                        case "table" when entry is ProxyPluginTable table && update.TableValue != null:
                            if (!TryUpdateTableInPlace(table.Value, update.TableValue))
                            {
                                var oldTable = table.Value;
                                table.Value = RebuildDataTable(update.TableValue);
                                oldTable?.Dispose();
                            }
                            break;
                    }
                }
            }
        }

        public void OnPluginError(string pluginId, string errorMessage)
        {
            Logger.Error("Plugin {PluginId} error: {Error}", pluginId, errorMessage);
            OnError?.Invoke(pluginId, errorMessage);
        }

        public void OnPerformanceUpdate(List<PluginPerformanceDto> performances)
        {
            foreach (var perf in performances)
            {
                _performanceData[perf.PluginId] = perf.UpdateTimeMilliseconds;
            }

            var first = performances.FirstOrDefault();
            if (first != null)
            {
                _pushedCpuPercent = first.CpuPercent;
            }
        }

        public void OnImageResize(string pluginId, ImageDescriptorDto descriptor)
        {
            Logger.Information("Image resize received: {PluginId}/{ImageId} -> {W}x{H} (MMF: {MmfName})",
                pluginId, descriptor.Id, descriptor.Width, descriptor.Height, descriptor.MmfName);

            if (!_proxyImages.TryGetValue(pluginId, out var images)) return;

            // Replace proxy FIRST so that any cache recreation picks up the new one
            var oldIndex = images.FindIndex(img => img.ImageId == descriptor.Id);
            if (oldIndex >= 0)
            {
                var oldProxy = images[oldIndex];
                try
                {
                    var newProxy = new ProxyPluginImage(pluginId, descriptor);
                    images[oldIndex] = newProxy;

                    // Invalidate cache AFTER proxy is replaced
                    Cache.InvalidatePluginImages(pluginId, [descriptor.Id]);

                    oldProxy.Dispose();
                    Logger.Information("Replaced proxy image for {PluginId}/{ImageId}", pluginId, descriptor.Id);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to create replacement proxy image for {PluginId}/{ImageId}", pluginId, descriptor.Id);
                }
            }
        }

        #endregion

        private static ProxyPluginSensor CreateProxySensor(EntryDto dto)
        {
            var sensor = new ProxyPluginSensor(dto.Id, dto.Name, dto.SensorValue?.Unit);
            if (dto.SensorValue != null)
            {
                sensor.Value = dto.SensorValue.Value;
                sensor.ValueMin = dto.SensorValue.ValueMin;
                sensor.ValueMax = dto.SensorValue.ValueMax;
                sensor.ValueAvg = dto.SensorValue.ValueAvg;
            }
            return sensor;
        }

        private static ProxyPluginText CreateProxyText(EntryDto dto)
        {
            var text = new ProxyPluginText(dto.Id, dto.Name);
            text.Value = dto.TextValue ?? "";
            return text;
        }

        private static ProxyPluginTable CreateProxyTable(EntryDto dto)
        {
            var table = new ProxyPluginTable(dto.Id, dto.Name, dto.TableValue?.DefaultFormat ?? "");
            if (dto.TableValue != null)
            {
                table.Value = RebuildDataTable(dto.TableValue);
            }
            return table;
        }

        private static bool TryUpdateTableInPlace(DataTable existing, TableValueDto tableDto)
        {
            if (existing == null
                || existing.Columns.Count != tableDto.Columns.Count
                || existing.Rows.Count != tableDto.Rows.Count)
            {
                return false;
            }

            for (int r = 0; r < tableDto.Rows.Count; r++)
            {
                var rowCells = tableDto.Rows[r];
                var row = existing.Rows[r];

                for (int c = 0; c < rowCells.Count && c < existing.Columns.Count; c++)
                {
                    var cell = rowCells[c];
                    var current = row[c];

                    if (cell.Type == "sensor" && cell.SensorValue != null && current is ProxyPluginSensor sensor)
                    {
                        sensor.Value = cell.SensorValue.Value;
                        sensor.ValueMin = cell.SensorValue.ValueMin;
                        sensor.ValueMax = cell.SensorValue.ValueMax;
                        sensor.ValueAvg = cell.SensorValue.ValueAvg;
                    }
                    else if (cell.Type == "text" && current is ProxyPluginText text)
                    {
                        text.Value = cell.TextValue ?? "";
                    }
                    else
                    {
                        // Type mismatch — fall back to full rebuild
                        return false;
                    }
                }
            }

            return true;
        }

        private static DataTable RebuildDataTable(TableValueDto tableDto)
        {
            var dt = new DataTable();
            foreach (var col in tableDto.Columns)
            {
                dt.Columns.Add(col, typeof(object));
            }

            foreach (var rowCells in tableDto.Rows)
            {
                var row = dt.NewRow();
                for (int i = 0; i < rowCells.Count && i < dt.Columns.Count; i++)
                {
                    var cell = rowCells[i];
                    if (cell.Type == "sensor" && cell.SensorValue != null)
                    {
                        var proxySensor = new ProxyPluginSensor(
                            cell.SensorName ?? $"cell-{i}",
                            cell.SensorName ?? $"Cell {i}",
                            cell.SensorUnit);
                        proxySensor.Value = cell.SensorValue.Value;
                        proxySensor.ValueMin = cell.SensorValue.ValueMin;
                        proxySensor.ValueMax = cell.SensorValue.ValueMax;
                        proxySensor.ValueAvg = cell.SensorValue.ValueAvg;
                        row[i] = proxySensor;
                    }
                    else
                    {
                        var proxyText = new ProxyPluginText(
                            cell.TextValue ?? $"cell-{i}",
                            cell.TextValue ?? "");
                        proxyText.Value = cell.TextValue ?? "";
                        row[i] = proxyText;
                    }
                }
                dt.Rows.Add(row);
            }

            return dt;
        }

        private async Task KillProcessAsync()
        {
            _jsonRpc?.Dispose();
            _jsonRpc = null;

            _pipeServer?.Dispose();
            _pipeServer = null;

            if (_hostProcess != null)
            {
                if (!_hostProcess.HasExited)
                {
                    try
                    {
                        _hostProcess.Kill(entireProcessTree: true);
                        await _hostProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "Error killing host process");
                    }
                }
                _hostProcess.Dispose();
                _hostProcess = null;
            }
        }

        private static string GetHostExePath()
        {
            return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "InfoPanel.exe");
        }

        public void Dispose()
        {
            _privateWorkingSetCounter?.Dispose();
            _privateWorkingSetCounter = null;

            // Invalidate image caches before disposing proxies
            InvalidateImageCaches();

            // Dispose image proxies
            foreach (var images in _proxyImages.Values)
            {
                foreach (var image in images)
                {
                    try { image.Dispose(); } catch { }
                }
            }
            _proxyImages.Clear();

            _jsonRpc?.Dispose();
            _jsonRpc = null;

            _pipeServer?.Dispose();
            _pipeServer = null;

            if (_hostProcess != null)
            {
                if (!_hostProcess.HasExited)
                {
                    try { _hostProcess.Kill(entireProcessTree: true); } catch { }
                }
                _hostProcess.Dispose();
                _hostProcess = null;
            }
        }
    }
}
