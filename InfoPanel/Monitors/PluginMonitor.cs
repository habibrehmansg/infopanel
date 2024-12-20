using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
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
    internal class PluginMonitor : BackgroundTask
    {
        private static readonly Lazy<PluginMonitor> _instance = new(() => new PluginMonitor());
        public static PluginMonitor Instance => _instance.Value;

        private static readonly ConcurrentDictionary<string, IPlugin> HEADER_DICT = new();
        public static readonly ConcurrentDictionary<string, PluginReading> SENSORHASH = new();

        private PluginMonitor() { }



        Dictionary<string, PluginWrapper> loadedPlugins = [];

        private async Task load()
        {
            PluginLoader pluginLoader = new();

            var currentDirectory = Directory.GetCurrentDirectory();
            var pluginPath = Path.Combine(currentDirectory, "..\\..\\..\\..\\..\\InfoPanel.Extras\\bin\\Debug\\net8.0-windows", "InfoPanel.Extras.dll");

            var plugins = pluginLoader.InitializePlugin(pluginPath);


            foreach (var plugin in plugins)
            {
                PluginWrapper pluginWrapper = new(plugin);
                if (loadedPlugins.TryAdd(pluginWrapper.Name, pluginWrapper))
                {
                    try
                    {
                        await pluginWrapper.Initialize();
                        Console.WriteLine($"Plugin {pluginWrapper.Name} loaded successfully");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Plugin {pluginWrapper.Name} failed to load: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"Plugin {pluginWrapper.Name} already loaded or duplicate plugin/name");
                }

                //break;
            }

        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);
            try
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                await load();

                stopwatch.Stop();
                Trace.WriteLine($"Computer open: {stopwatch.ElapsedMilliseconds}ms");

                try
                {

                    while (!token.IsCancellationRequested)
                    {
                        int indexOrder = 0;
                        foreach (var wrapper in loadedPlugins.Values)
                        {
                            wrapper.Update();

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
                    HEADER_DICT.Clear();
                    SENSORHASH.Clear();
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"LibreMonitor: Init error: {e.Message}");
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
