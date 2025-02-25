using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using InfoPanel.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Monitors
{
    internal class PluginMonitor: BackgroundTask
    {
        private static readonly Lazy<PluginMonitor> _instance = new(() => new PluginMonitor());
        public static PluginMonitor Instance => _instance.Value;

        public static readonly ConcurrentDictionary<string, PluginReading> SENSORHASH = new();

        public readonly Dictionary<string, PluginWrapper> _loadedPlugins = [];

        private PluginMonitor() { }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                await LoadPluginsAsync();
                stopwatch.Stop();
                Trace.WriteLine($"Plugins loaded: {stopwatch.ElapsedMilliseconds}ms");

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
                Trace.WriteLine("Task cancelled");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Exception during work: {ex.Message}");
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

        internal async Task LoadPluginsAsync()
        {
            PluginLoader pluginLoader = new();

            PluginStateHelper.InitialSetup();
            PluginStateHelper.UpdateValidation();
            var pluginStateList = PluginStateHelper.DecryptAndLoadStateList();            
            var pluginDirectory = PluginStateHelper.PluginsFolder;

            foreach (var directory in Directory.GetDirectories(pluginDirectory))
            {
                var ph = pluginStateList.FirstOrDefault(x => x.PluginName == Path.GetFileName(directory));
                if(ph?.Activated == false) continue;
                foreach (var pluginFile in Directory.GetFiles(directory, "InfoPanel.*.dll"))
                {
                    var plugins = pluginLoader.InitializePlugin(pluginFile);
                    await AddPlugins(plugins, pluginFile);                    
                }
            }
        }

        

        private async Task AddPlugins(IEnumerable<IPlugin> plugins, string pluginFile)
        {
            foreach (var plugin in plugins)
            {
                PluginWrapper wrapper = new(Path.GetFileName(pluginFile), plugin);
                if (_loadedPlugins.TryAdd(wrapper.Name, wrapper))
                {
                    try
                    {
                        await wrapper.Initialize();
                        Console.WriteLine($"Plugin {wrapper.Name} loaded successfully");

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
                        Console.WriteLine($"Plugin {wrapper.Name} failed to load: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"Plugin {wrapper.Name} already loaded or duplicate plugin/name");
                }
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
