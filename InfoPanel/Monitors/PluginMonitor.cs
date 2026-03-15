using InfoPanel.Monitors.PluginProxies;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Monitors
{
    internal class PluginMonitor : BackgroundTask
    {
        private static readonly ILogger Logger = Log.ForContext<PluginMonitor>();
        private static readonly Lazy<PluginMonitor> _instance = new(() => new PluginMonitor());
        public static PluginMonitor Instance => _instance.Value;

        public static readonly ConcurrentDictionary<string, PluginReading> SENSORHASH = new();

        public List<PluginDescriptor> Plugins { get; private set; } = [];

        internal PluginProcessManager ProcessManager { get; } = new();

        // Track remote wrappers per descriptor for ViewModel access
        internal readonly ConcurrentDictionary<string, List<RemotePluginWrapper>> RemoteWrappers = new();

        private PluginMonitor() {
            if(!Directory.Exists(FileUtil.GetExternalPluginFolder()))
            {
                Directory.CreateDirectory(FileUtil.GetExternalPluginFolder());
            }
        }

        /// <summary>
        /// Persists the list of deactivated plugins. A plugin is considered "deactivated" when
        /// its host process is NOT currently running, so we save those file paths to disk.
        /// On next startup, plugins in this list will be skipped.
        /// </summary>
        public void SavePluginState()
        {
            try
            {
                var deactivatedPlugins = Plugins
                    .Where(p => !ProcessManager.IsHostRunning(p.FilePath))
                    .Select(p => p.FilePath)
                    .ToList();
                File.WriteAllLines(FileUtil.GetPluginStateFile(), deactivatedPlugins);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to save plugin state");
            }
        }

        public string[] GetPluginState()
        {
            if (File.Exists(FileUtil.GetPluginStateFile()))
            {
                try
                {
                    return File.ReadAllLines(FileUtil.GetPluginStateFile());
                }
                catch { }
            }

            return [];
        }

        private void UnzipPluginArchives()
        {
            try
            {
                foreach (var file in Directory.GetFiles(FileUtil.GetExternalPluginFolder(), "InfoPanel.*.zip"))
                {
                    UnzipPluginArchive(file);
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to unzip plugin archives");
            }
        }

        private static bool UnzipPluginArchive(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open);
            using var za = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = za.Entries[0];

            if (!Regex.IsMatch(entry.FullName, "InfoPanel.[a-zA-Z0-9]+\\/"))
            {
                return false;
            }

            za.ExtractToDirectory(FileUtil.GetExternalPluginFolder(), true);
            return true;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                FindPlugins();

                var deactivatedPlugins = GetPluginState();
                foreach (var descriptor in Plugins)
                {
                    if(deactivatedPlugins.Contains(descriptor.FilePath))
                    {
                        continue;
                    }

                    await StartPluginModulesAsync(descriptor);
                }

                stopwatch.Stop();
                Logger.Information("Plugins loaded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);

                // No polling loop needed - host processes handle their own update scheduling
                // Metrics loop is started on-demand when the plugins page is visible.
                // Just keep alive until cancellation
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                }
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("PluginMonitor task cancelled");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Exception during PluginMonitor work");
            }
            finally
            {
                await ProcessManager.StopMetricsLoopAsync();
                await ProcessManager.StopAllAsync();
                ProcessManager.Dispose();
            }
        }

        private static readonly string[] _bundledPlugins = [Path.Combine("plugins", "InfoPanel.Extras")];
        internal void FindPlugins()
        {
            UnzipPluginArchives();
            //bundled plugins
            foreach (var directory in Directory.GetDirectories(FileUtil.GetBundledPluginFolder()))
            {
                //whitelist plugins
                if (_bundledPlugins.Contains(directory))
                {
                    Plugins.Add(CreatePluginDescriptor(directory));
                }
            }

            //external plugins
            foreach (var directory in Directory.GetDirectories(FileUtil.GetExternalPluginFolder()))
            {
                Plugins.Add(CreatePluginDescriptor(directory));
            }
        }

        internal PluginDescriptor CreatePluginDescriptor(string directory)
        {
            var pluginInfo = PluginLoader.GetPluginInfo(directory);
            //lock plugin convention to <FolderName>.dll
            var pluginFile = Path.Combine(directory, Path.GetFileName(directory) + ".dll");
            var pluginDescriptor = new PluginDescriptor(pluginFile, pluginInfo);

            // Don't load the assembly in the main process - host process will do that
            return pluginDescriptor;
        }

        public async Task StopPluginModulesAsync(PluginDescriptor pluginDescriptor)
        {
            // Clean up SENSORHASH entries
            var keysToRemove = SENSORHASH.Keys
                .Where(k =>
                {
                    if (RemoteWrappers.TryGetValue(pluginDescriptor.FilePath, out var wrappers))
                        return wrappers.Any(w => k.StartsWith($"/{w.Id}/"));
                    return false;
                })
                .ToList();

            foreach (var key in keysToRemove)
            {
                SENSORHASH.TryRemove(key, out _);
            }

            RemoteWrappers.TryRemove(pluginDescriptor.FilePath, out _);
            await ProcessManager.StopHostAsync(pluginDescriptor);
        }

        public async Task StartPluginModulesAsync(PluginDescriptor pluginDescriptor)
        {
            try
            {
                var connection = await ProcessManager.LaunchHostAsync(pluginDescriptor);

                // Register proxy entries in SENSORHASH
                PluginProcessManager.RegisterProxiesInSensorHash(pluginDescriptor, connection);

                // Build remote wrappers for ViewModel access
                var wrappers = new List<RemotePluginWrapper>();
                if (connection.PluginMetadata != null)
                {
                    foreach (var metadata in connection.PluginMetadata)
                    {
                        wrappers.Add(new RemotePluginWrapper(connection, metadata));
                    }
                }
                RemoteWrappers[pluginDescriptor.FilePath] = wrappers;

                Logger.Information("Plugin modules started for {PluginPath}", pluginDescriptor.FilePath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start plugin modules for {PluginPath}", pluginDescriptor.FilePath);
            }
        }

        public async Task ReloadPluginModule(PluginDescriptor pluginDescriptor)
        {
            await StopPluginModulesAsync(pluginDescriptor);
            await StartPluginModulesAsync(pluginDescriptor);
        }

        public static List<PluginReading> GetOrderedList()
        {
            List<PluginReading> OrderedList = [.. SENSORHASH.Values.OrderBy(x => x.IndexOrder)];
            return OrderedList;
        }

        /// <summary>
        /// Gets a plugin image proxy by plugin ID and image ID.
        /// Returns null if not found.
        /// </summary>
        public ProxyPluginImage? GetImageProxy(string pluginId, string imageId)
        {
            foreach (var conn in ProcessManager.GetAllConnections())
            {
                var images = conn.GetProxyImages(pluginId);
                var match = images.FirstOrDefault(i => i.ImageId == imageId);
                if (match != null) return match;
            }
            return null;
        }

        /// <summary>
        /// Gets all plugin image proxies across all connections.
        /// </summary>
        public List<ProxyPluginImage> GetAllImageProxies()
        {
            var result = new List<ProxyPluginImage>();
            foreach (var conn in ProcessManager.GetAllConnections())
            {
                result.AddRange(conn.GetAllProxyImages());
            }
            return result;
        }

        public record struct PluginReading
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ContainerId { get; set; }
            public string ContainerName { get; set; }
            public string PluginId { get; set; }
            public string PluginName { get; set; }
            public IPluginData Data { get; set; }
            public int IndexOrder { get; set; }
        }
    }
}
