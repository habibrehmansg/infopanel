using Flurl.Http;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using Microsoft.Win32.TaskScheduler.Fluent;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public readonly Dictionary<string, PluginWrapper> _loadedPlugins = [];

        private PluginMonitor() {
            if(!Directory.Exists(FileUtil.GetExternalPluginFolder()))
            {
                Directory.CreateDirectory(FileUtil.GetExternalPluginFolder());
            }
        }

        public void SavePluginState()
        {
            try
            {
                var deactivatedPlugins = Plugins.Where(p => p.PluginWrappers.All(w => !w.Value.IsRunning)).Select(p => p.FilePath).ToList();
                File.WriteAllLines(FileUtil.GetPluginStateFile(), deactivatedPlugins);
            }
            catch { }
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
            catch (Exception e) { }
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

            //if (Directory.Exists(Path.Combine(FileUtil.GetExternalPluginFolder(), entry.FullName)))
            //{
            //    return false;
            //}

            za.ExtractToDirectory(FileUtil.GetExternalPluginFolder(), true);
            return true;
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                //await LoadAllPluginsAsync();
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

                while (!token.IsCancellationRequested)
                {
                    stopwatch.Restart();
                    foreach (var plugin in _loadedPlugins.Values)
                    {
                        try
                        {
                            //plugin.Update();
                        }
                        catch { }
                    }
                    stopwatch.Stop();
                    //Trace.WriteLine($"Plugins updated: {stopwatch.ElapsedMilliseconds}ms");
                    await Task.Delay(100, token);
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
                foreach (var loadedPluginWrapper in _loadedPlugins.Values)
                {
                    await loadedPluginWrapper.StopAsync();
                }

                _loadedPlugins.Clear();
            }
        }

        internal void FindPlugins()
        {
            UnzipPluginArchives();
            //bundled plugins
            foreach (var directory in Directory.GetDirectories(FileUtil.GetBundledPluginFolder()))
            {
                Plugins.Add(CreatePluginDescriptor(directory));
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

            var plugins = PluginLoader.InitializePlugin(pluginFile);

            foreach (var plugin in plugins)
            {
                PluginWrapper wrapper = new(pluginDescriptor, plugin);
                pluginDescriptor.PluginWrappers.TryAdd(wrapper.Id, wrapper);
            }

            return pluginDescriptor;
        }

        public async Task StopPluginModulesAsync(PluginDescriptor pluginDescriptor)
        {
            foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
            {
                foreach (var container in wrapper.PluginContainers)
                {
                    foreach (var entry in container.Entries)
                    {
                        var id = $"/{wrapper.Id}/{container.Id}/{entry.Id}";
                        SENSORHASH.TryRemove(id, out _);
                    }
                }

                await wrapper.StopAsync();
            }
        }

        public async Task StartPluginModulesAsync(PluginDescriptor pluginDescriptor)
        {
            foreach (var wrapper in pluginDescriptor.PluginWrappers.Values)
            {
                try
                {
                    await wrapper.Initialize();
                    Log.Information("Plugin {PluginName} loaded successfully", wrapper.Name);

                    int indexOrder = 0;
                    foreach (var container in wrapper.PluginContainers)
                    {
                        foreach (var entry in container.Entries)
                        {
                            var id = $"/{wrapper.Id}/{container.Id}/{entry.Id}";
                            SENSORHASH[id] = new()
                            {
                                Id = id,
                                Name = entry.Name,
                                ContainerId = container.Id,
                                ContainerName = container.Name,
                                PluginId = wrapper.Id,
                                PluginName = wrapper.Name,
                                Data = entry,
                                IndexOrder = indexOrder++
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Plugin {PluginName} failed to load", wrapper.Name);
                }
            }
        }



        public async Task ReloadPluginModule(PluginWrapper wrapper)
        {
            foreach (var container in wrapper.PluginContainers)
            {
                foreach (var entry in container.Entries)
                {
                    var id = $"/{wrapper.Id}/{container.Id}/{entry.Id}";
                    SENSORHASH.TryRemove(id, out _);
                }
            }

            await wrapper.StopAsync();

            try
            {
                await wrapper.Initialize();
                Log.Information("Plugin {PluginName} reloaded successfully", wrapper.Name);

                int indexOrder = 0;
                foreach (var container in wrapper.PluginContainers)
                {
                    foreach (var entry in container.Entries)
                    {
                        var id = $"/{wrapper.Id}/{container.Id}/{entry.Id}";
                        SENSORHASH[id] = new()
                        {
                            Id = id,
                            Name = entry.Name,
                            ContainerId = container.Id,
                            ContainerName = container.Name,
                            PluginId = wrapper.Id,
                            PluginName = wrapper.Name,
                            Data = entry,
                            IndexOrder = indexOrder++
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin {PluginName} failed to load", wrapper.Name);
            }
        }

        private async Task LoadPlugin(PluginWrapper wrapper)
        {
            try
            {
                await wrapper.Initialize();
                Log.Information("Plugin {PluginName} loaded successfully", wrapper.Name);

                int indexOrder = 0;
                foreach (var container in wrapper.PluginContainers)
                {
                    foreach (var entry in container.Entries)
                    {
                        var id = $"/{wrapper.Id}/{container.Id}/{entry.Id}";
                        SENSORHASH[id] = new()
                        {
                            Id = id,
                            Name = entry.Name,
                            ContainerId = container.Id,
                            ContainerName = container.Name,
                            PluginId = wrapper.Id,
                            PluginName = wrapper.Name,
                            Data = entry,
                            IndexOrder = indexOrder++
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin {PluginName} failed to load", wrapper.Name);
            }
        }

        public static List<PluginReading> GetOrderedList()
        {
            List<PluginReading> OrderedList = [.. SENSORHASH.Values.OrderBy(x => x.IndexOrder)];
            return OrderedList;
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
