using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using System.Collections;
using System.Diagnostics;
using System.Text;


// The plugin to run, as arguments: the plugin's DLL, then the id of the plugin in it (without an id, every plugin in it).
if (args.Length is < 1 or > 2)
{
    Console.WriteLine("Usage: InfoPanel.Plugins.Simulator <path to the plugin's DLL> [plugin id]");
    return 1;
}

var pluginPath = Path.GetFullPath(args[0]);
var pluginFolder = Path.GetDirectoryName(pluginPath)!;
var targetPlugin = args.Length > 1 ? args[1] : null;
var plugins = PluginLoader.InitializePlugin(pluginPath).ToList();

// An id the DLL doesn't have is an error here, before the display loop starts.
var selected = SelectPlugins(plugins, targetPlugin);
if (selected.Count == 0)
{
    var ids = string.Join(", ", plugins.Select(plugin => plugin.Id));
    Console.WriteLine(targetPlugin == null
        ? $"No plugin found in {pluginPath}"
        : $"No plugin with the id '{targetPlugin}' in {pluginPath}. Its plugin ids: {(ids.Length == 0 ? "(none)" : ids)}");
    return 1;
}

var pluginInfo = PluginLoader.GetPluginInfo(pluginFolder);
var pluginDescriptor = new PluginDescriptor(pluginPath, pluginInfo);

foreach (var plugin in selected)
{

    PluginWrapper pluginWrapper = new(pluginDescriptor, plugin);

    if (pluginDescriptor.PluginWrappers.TryAdd(pluginWrapper.Id, pluginWrapper))
    {
        try
        {
            await pluginWrapper.Initialize();
            Console.WriteLine($"Plugin {pluginWrapper.Name} loaded successfully");
        }
        catch (Exception ex)
        {
            // A plugin that failed to initialise must not reach the count check or the screens.
            pluginDescriptor.PluginWrappers.Remove(pluginWrapper.Id);
            Console.WriteLine($"Plugin {pluginWrapper.Name} failed to load: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine($"Plugin {pluginWrapper.Name} already loaded or duplicate plugin/name");
    }
}

if (pluginDescriptor.PluginWrappers.Count == 0)
{
    Console.WriteLine("No plugin could be loaded.");
    return 1;
}

Thread.Sleep(1000);
Console.Clear();

string lastOutput = string.Empty;

while (true)
{
    // One screen for every plugin, printed once per round.
    var output = BuildScreen(pluginDescriptor.PluginWrappers.Values);

    // Only update the console if the output has changed, to reduce flicker
    if (output != lastOutput)
    {
        lastOutput = output;
        Console.Clear();
        Console.WriteLine(output);
    }

    Thread.Sleep(30);
}

// The plugins to run: the one with the given id, or every plugin in the DLL without an id.
static List<IPlugin> SelectPlugins(IEnumerable<IPlugin> plugins, string? id) =>
    plugins.Where(plugin => id == null || plugin.Id == id).ToList();

// The text of the whole screen: every plugin updated, then listed after the one before.
static string BuildScreen(IEnumerable<PluginWrapper> wrappers)
{
    StringBuilder buffer = new();

    foreach (var wrapper in wrappers)
    {
        wrapper.Update();

        buffer.AppendLine($"-{wrapper.Name} ({wrapper.Plugin.GetType().FullName}) [UpdateInterval={wrapper.UpdateInterval.TotalMilliseconds}ms, UpdateTime={wrapper.UpdateTimeMilliseconds}ms]");

        foreach (var container in wrapper.PluginContainers)
        {
            buffer.AppendLine($"--{container.Name}");
            foreach (var entry in container.Entries)
            {
                if (entry is IPluginText text)
                {
                    buffer.AppendLine($"---{text.Name}: {text.Value}");
                }
                else if (entry is IPluginSensor sensor)
                {
                    buffer.AppendLine($"---{sensor.Name}: {sensor.Value.ToString()}{sensor.Unit}");
                }
                else if (entry is IPluginTable table)
                {
                    buffer.AppendLine($"---{table.Name}: {table.ToString()}");
                }
                else if (entry is IPluginImage image)
                {
                    // Its size only: InfoPanel 2.0 shows the picture; a console shows that there is one.
                    buffer.AppendLine($"---{image.Name}: {(image.Value.IsEmpty ? "no picture" : $"a picture of {image.Value.Length} bytes")}");
                }
            }

            buffer.AppendLine();
        }
    }

    return buffer.ToString();
}
