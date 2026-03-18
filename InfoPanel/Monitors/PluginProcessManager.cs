using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Monitors.PluginProxies;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using Serilog;

namespace InfoPanel.Monitors
{
    /// <summary>
    /// Manages all plugin host process connections.
    /// </summary>
    internal class PluginProcessManager : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<PluginProcessManager>();

        private readonly JobObject _jobObject = new();
        private readonly ConcurrentDictionary<string, PluginHostConnection> _connections = new();
        private readonly ConcurrentDictionary<string, int> _retryCounts = new();
        private readonly ConcurrentDictionary<string, DateTime> _retryWindows = new();
        private readonly ConcurrentDictionary<string, bool> _restarting = new();

        private CancellationTokenSource? _metricsCts;
        private Task? _metricsTask;

        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(60);

        public async Task<PluginHostConnection> LaunchHostAsync(PluginDescriptor descriptor, CancellationToken token = default)
        {
            var connection = new PluginHostConnection(descriptor.FilePath, _jobObject);

            connection.OnDisconnected += () => OnHostDisconnected(descriptor);

            try
            {
                var metadata = await connection.StartAsync(token);
                _connections[descriptor.FilePath] = connection;
                _retryCounts[descriptor.FilePath] = 0;

                Logger.Information("Plugin host launched for {PluginPath} with {Count} plugins",
                    descriptor.FilePath, metadata.Count);

                return connection;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to launch plugin host for {PluginPath}", descriptor.FilePath);
                connection.Dispose();
                throw;
            }
        }

        public async Task StopHostAsync(PluginDescriptor descriptor)
        {
            if (_connections.TryRemove(descriptor.FilePath, out var connection))
            {
                try
                {
                    await connection.StopAsync();
                }
                finally
                {
                    connection.Dispose();
                }
            }
        }

        public async Task StopAllAsync()
        {
            // Snapshot and clear immediately to prevent new operations on stale connections
            var snapshot = _connections.ToArray();
            _connections.Clear();

            var tasks = snapshot.Select(kvp => Task.Run(async () =>
            {
                try
                {
                    await kvp.Value.StopAsync();
                }
                finally
                {
                    kvp.Value.Dispose();
                }
            }));

            // Cap total shutdown time so the app exits promptly.
            // The job object will kill any remaining child processes.
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(2)));
        }

        public void StartMetricsLoop()
        {
            _metricsCts = new CancellationTokenSource();
            _metricsTask = Task.Run(() => MetricsLoopAsync(_metricsCts.Token));
        }

        public async Task StopMetricsLoopAsync()
        {
            if (_metricsCts != null)
            {
                _metricsCts.Cancel();
                if (_metricsTask != null)
                    try { await _metricsTask; } catch (OperationCanceledException) { }
                _metricsCts.Dispose();
                _metricsCts = null;
                _metricsTask = null;
            }
        }

        private async Task MetricsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    InitializeMetricsCounters();

                    foreach (var conn in _connections.Values)
                    {
                        conn.UpdateCachedMetrics();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Failed to refresh metrics counters");
                }

                await Task.Delay(2000, token);
            }
        }

        private void InitializeMetricsCounters()
        {
            // Collect connections that still need a perf counter, grouped by process name
            var needsInit = new List<(int Pid, string ProcessName, PluginHostConnection Connection)>();
            foreach (var conn in _connections.Values)
            {
                if (!conn.HasMetricsCounter)
                {
                    var pid = conn.GetProcessId();
                    var name = conn.GetProcessName();
                    if (pid > 0 && name != null)
                        needsInit.Add((pid, name, conn));
                }
            }

            if (needsInit.Count == 0) return;

            // Group by process name so we only probe instances matching each name
            var byName = needsInit.GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase);

            foreach (var group in byName)
            {
                var processName = group.Key;
                var pidMap = group.ToDictionary(x => x.Pid, x => x.Connection);

                // Perf counter instance names: "Name", "Name#1", "Name#2", ...
                // Try the base name first, then numbered suffixes
                for (int i = 0; pidMap.Count > 0; i++)
                {
                    var instance = i == 0 ? processName : $"{processName}#{i}";
                    try
                    {
                        using var idCounter = new PerformanceCounter("Process", "ID Process", instance, readOnly: true);
                        var pid = (int)idCounter.RawValue;
                        if (pidMap.TryGetValue(pid, out var conn))
                        {
                            conn.SetPrivateWorkingSetCounter(
                                new PerformanceCounter("Process", "Working Set - Private", instance, readOnly: true));
                            pidMap.Remove(pid);
                        }
                    }
                    catch
                    {
                        // Instance doesn't exist — no more instances with this name
                        break;
                    }
                }
            }
        }

        public void Dispose()
        {
            _metricsCts?.Cancel();
            _metricsCts?.Dispose();
            _jobObject.Dispose();
        }

        public PluginHostConnection? GetConnection(string filePath)
        {
            _connections.TryGetValue(filePath, out var connection);
            return connection;
        }

        public IEnumerable<PluginHostConnection> GetAllConnections()
        {
            return _connections.Values;
        }

        public bool IsHostRunning(string filePath)
        {
            return _connections.TryGetValue(filePath, out var conn) && conn.IsProcessRunning && conn.IsConnected;
        }

        public List<PluginMetadataDto> GetPluginMetadata(string filePath)
        {
            if (_connections.TryGetValue(filePath, out var conn))
                return conn.PluginMetadata ?? [];
            return [];
        }

        public List<ProxyPluginContainer> GetProxyContainers(string filePath, string pluginId)
        {
            if (_connections.TryGetValue(filePath, out var conn))
                return conn.GetProxyContainers(pluginId);
            return [];
        }

        public ProcessMetrics? GetProcessMetrics(string filePath)
        {
            if (_connections.TryGetValue(filePath, out var conn))
                return conn.ProcessMetrics;
            return null;
        }

        public long GetUpdateTimeMilliseconds(string filePath, string pluginId)
        {
            if (_connections.TryGetValue(filePath, out var conn))
                return conn.GetUpdateTimeMilliseconds(pluginId);
            return 0;
        }

        private void OnHostDisconnected(PluginDescriptor descriptor)
        {
            // If the connection was already removed (intentional stop), do nothing
            if (!_connections.ContainsKey(descriptor.FilePath))
            {
                Logger.Debug("Host disconnected for {PluginPath} after intentional stop, not restarting", descriptor.FilePath);
                return;
            }

            // Guard against concurrent restart attempts for the same plugin
            if (!_restarting.TryAdd(descriptor.FilePath, true))
            {
                Logger.Debug("Restart already in progress for {PluginPath}, skipping", descriptor.FilePath);
                return;
            }

            Logger.Warning("Host disconnected unexpectedly for {PluginPath}, checking retry policy", descriptor.FilePath);

            // Clean up SENSORHASH entries for this plugin
            CleanupSensorHash(descriptor);

            // Check retry policy
            var now = DateTime.UtcNow;
            if (!_retryWindows.TryGetValue(descriptor.FilePath, out var windowStart) ||
                (now - windowStart) > RetryWindow)
            {
                _retryWindows[descriptor.FilePath] = now;
                _retryCounts[descriptor.FilePath] = 0;
            }

            var retryCount = _retryCounts.GetOrAdd(descriptor.FilePath, 0);
            if (retryCount >= MaxRetries)
            {
                Logger.Error("Max retries ({MaxRetries}) exceeded for {PluginPath}, not restarting",
                    MaxRetries, descriptor.FilePath);
                _restarting.TryRemove(descriptor.FilePath, out _);
                return;
            }

            _retryCounts[descriptor.FilePath] = retryCount + 1;

            // Schedule restart
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000); // Brief delay before restart

                    Logger.Information("Auto-restarting plugin host for {PluginPath} (attempt {Attempt}/{Max})",
                        descriptor.FilePath, retryCount + 1, MaxRetries);

                    // Clean up old connection
                    if (_connections.TryRemove(descriptor.FilePath, out var oldConn))
                    {
                        oldConn.Dispose();
                    }

                    var connection = await LaunchHostAsync(descriptor);
                    RegisterProxiesInSensorHash(descriptor, connection);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to auto-restart plugin host for {PluginPath}", descriptor.FilePath);
                }
                finally
                {
                    _restarting.TryRemove(descriptor.FilePath, out _);
                }
            });
        }

        private static void CleanupSensorHash(PluginDescriptor descriptor)
        {
            if (PluginMonitor.Instance.RemoteWrappers.TryGetValue(descriptor.FilePath, out var wrappers))
            {
                var pluginIds = wrappers.Select(w => w.Id).ToList();
                var keysToRemove = PluginMonitor.SENSORHASH.Keys
                    .Where(k => pluginIds.Any(id => k.StartsWith($"/{id}/")))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    PluginMonitor.SENSORHASH.TryRemove(key, out _);
                }
            }
        }

        internal static void RegisterProxiesInSensorHash(PluginDescriptor descriptor, PluginHostConnection connection)
        {
            if (connection.PluginMetadata == null) return;

            foreach (var metadata in connection.PluginMetadata)
            {
                var containers = connection.GetProxyContainers(metadata.Id);
                int indexOrder = 0;

                foreach (var container in containers)
                {
                    foreach (var entry in container.Entries)
                    {
                        var id = container.IsEphemeralPath
                            ? $"/{metadata.Id}/{entry.Id}"
                            : $"/{metadata.Id}/{container.Id}/{entry.Id}";

                        PluginMonitor.SENSORHASH[id] = new PluginMonitor.PluginReading
                        {
                            Id = id,
                            Name = entry.Name,
                            ContainerId = container.Id,
                            ContainerName = container.Name,
                            PluginId = metadata.Id,
                            PluginName = metadata.Name,
                            Data = entry,
                            IndexOrder = indexOrder++
                        };
                    }
                }
            }
        }
    }
}
