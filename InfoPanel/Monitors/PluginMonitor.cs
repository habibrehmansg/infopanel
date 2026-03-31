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

        internal readonly object PluginsLock = new();
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
                List<string> deactivatedPlugins;
                lock (PluginsLock)
                {
                    deactivatedPlugins = Plugins
                        .Where(p => !ProcessManager.IsHostRunning(p.FilePath))
                        .Select(p => p.FilePath)
                        .ToList();
                }
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
                    var extracted = UnzipPluginArchive(file);
                    if (extracted != null)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to unzip plugin archives");
            }
        }

        /// <summary>
        /// Extracts a plugin ZIP archive to the external plugin folder.
        /// Handles both structured ZIPs (InfoPanel.Name/files) and flat ZIPs (files at root).
        /// Returns the extracted folder path, or null on failure.
        /// </summary>
        internal static string? UnzipPluginArchive(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open);
            using var za = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = za.Entries[0];

            var match = Regex.Match(entry.FullName, @"(InfoPanel\.[a-zA-Z0-9]+)\/");
            if (match.Success)
            {
                // Structured ZIP: has InfoPanel.Name/ folder inside
                za.ExtractToDirectory(FileUtil.GetExternalPluginFolder(), true);
                return Path.Combine(FileUtil.GetExternalPluginFolder(), match.Groups[1].Value);
            }

            // Flat ZIP: DLLs at root. Derive folder name from ZIP filename (e.g. InfoPanel.MyPlugin.zip -> InfoPanel.MyPlugin)
            var zipFileName = Path.GetFileNameWithoutExtension(filePath);
            if (!Regex.IsMatch(zipFileName, @"^InfoPanel\.[a-zA-Z0-9]+$"))
            {
                return null;
            }

            var targetFolder = Path.Combine(FileUtil.GetExternalPluginFolder(), zipFileName);
            Directory.CreateDirectory(targetFolder);
            za.ExtractToDirectory(targetFolder, true);
            return targetFolder;
        }

        /// <summary>
        /// Peeks into a ZIP to determine the plugin folder name without extracting.
        /// Returns the folder name (e.g. "InfoPanel.Weather") or null.
        /// </summary>
        private static string? PeekZipPluginFolderName(string zipFilePath)
        {
            try
            {
                using var fs = new FileStream(zipFilePath, FileMode.Open, FileAccess.Read);
                using var za = new ZipArchive(fs, ZipArchiveMode.Read);
                if (za.Entries.Count == 0) return null;

                var entry = za.Entries[0];
                var match = Regex.Match(entry.FullName, @"(InfoPanel\.[a-zA-Z0-9]+)\/");
                if (match.Success)
                    return match.Groups[1].Value;

                // Flat ZIP: derive from filename
                var zipFileName = Path.GetFileNameWithoutExtension(zipFilePath);
                if (Regex.IsMatch(zipFileName, @"^InfoPanel\.[a-zA-Z0-9]+$"))
                    return zipFileName;
            }
            catch (Exception) { }

            return null;
        }

        /// <summary>
        /// Installs a plugin from a ZIP file at runtime. Stops the existing plugin first
        /// (if upgrading), extracts, creates descriptor, and starts the new plugin.
        /// </summary>
        public async Task<PluginDescriptor?> InstallPluginFromZipAsync(string zipFilePath)
        {
            // Determine the target folder name from the ZIP before extracting,
            // so we can stop any running plugin that would lock the DLLs.
            var targetFolderName = PeekZipPluginFolderName(zipFilePath);
            if (targetFolderName != null)
            {
                PluginDescriptor? running;
                lock (PluginsLock)
                {
                    running = Plugins.FirstOrDefault(p =>
                        string.Equals(p.FolderName, targetFolderName, StringComparison.OrdinalIgnoreCase));
                }

                if (running != null)
                {
                    await StopPluginModulesAsync(running);
                    lock (PluginsLock)
                    {
                        Plugins.Remove(running);
                    }
                }
            }

            var extractedFolder = UnzipPluginArchive(zipFilePath);
            if (extractedFolder == null)
            {
                Logger.Warning("Failed to extract plugin from {ZipPath}", zipFilePath);
                return null;
            }

            try
            {
                File.Delete(zipFilePath);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to delete ZIP after extraction: {ZipPath}", zipFilePath);
            }

            var descriptor = CreatePluginDescriptor(extractedFolder);

            lock (PluginsLock)
            {
                Plugins.Add(descriptor);
            }

            // Auto-start the newly installed plugin
            await StartPluginModulesAsync(descriptor);
            SavePluginState();

            Logger.Information("Plugin installed from ZIP: {PluginPath}", descriptor.FilePath);
            return descriptor;
        }

        /// <summary>
        /// Uninstalls a plugin at runtime. Stops it if running, removes from list,
        /// cleans up state, and deletes the folder from disk.
        /// </summary>
        public async Task UninstallPluginAsync(PluginDescriptor descriptor)
        {
            if (ProcessManager.IsHostRunning(descriptor.FilePath))
            {
                await StopPluginModulesAsync(descriptor);
            }

            lock (PluginsLock)
            {
                Plugins.Remove(descriptor);
            }
            SavePluginState();

            if (descriptor.FolderPath != null && Directory.Exists(descriptor.FolderPath))
            {
                try
                {
                    Directory.Delete(descriptor.FolderPath, true);
                    Logger.Information("Plugin folder deleted: {FolderPath}", descriptor.FolderPath);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to delete plugin folder: {FolderPath}", descriptor.FolderPath);
                }
            }
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                FindPlugins();

                var deactivatedPlugins = GetPluginState();
                List<PluginDescriptor> pluginsSnapshot;
                lock (PluginsLock)
                {
                    pluginsSnapshot = Plugins.ToList();
                }
                foreach (var descriptor in pluginsSnapshot)
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

        private static readonly string[] _bundledPlugins =
        [
            Path.Combine("plugins", "InfoPanel.Extras"),
            Path.Combine("plugins", "InfoPanel.StopWatch"),
        ];
        internal void FindPlugins()
        {
            UnzipPluginArchives();
            lock (PluginsLock)
            {
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
        }

        internal PluginDescriptor CreatePluginDescriptor(string directory)
        {
            var pluginInfo = PluginLoader.GetPluginInfo(directory);
            //lock plugin convention to <FolderName>.dll
            var pluginFile = Path.Combine(directory, Path.GetFileName(directory) + ".dll");
            var pluginDescriptor = new PluginDescriptor(pluginFile, pluginInfo);

            var hubFile = Path.Combine(directory, ".hub");
            if (File.Exists(hubFile))
            {
                pluginDescriptor.Slug = File.ReadAllText(hubFile).Trim();
            }

            // Don't load the assembly in the main process - host process will do that
            return pluginDescriptor;
        }

        internal static void WriteHubSlug(string folderPath, string slug)
        {
            File.WriteAllText(Path.Combine(folderPath, ".hub"), slug);
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

        /// <summary>Invokes a plugin action method on a loaded host (same RPC path as the Plugins UI).</summary>
        public async Task InvokePluginHotkeyActionAsync(string pluginId, string methodName)
        {
            RemotePluginWrapper? target = null;
            lock (PluginsLock)
            {
                foreach (var kvp in RemoteWrappers)
                {
                    target = kvp.Value.FirstOrDefault(w =>
                        string.Equals(w.Id, pluginId, StringComparison.OrdinalIgnoreCase)
                        && w.Actions.Exists(a => string.Equals(a.MethodName, methodName, StringComparison.Ordinal)));
                    if (target != null)
                        break;
                }
            }

            if (target == null)
            {
                Logger.Warning("Global hotkey: plugin {PluginId} not loaded or has no action {Method}", pluginId, methodName);
                return;
            }

            try
            {
                await target.InvokeActionAsync(methodName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Global hotkey: failed to invoke {PluginId}.{Method}", pluginId, methodName);
            }
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
