using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using System.Text;

//\InfoPanel\InfoPanel.Plugins.Simulator\bin\Debug\net8.0-windows

var currentDirectory = Directory.GetCurrentDirectory();

var pluginFolder = Path.Combine(currentDirectory, "..\\..\\..\\..\\..\\InfoPanel.Extras\\bin\\x64\\Debug\\net8.0-windows\\win-x64");

var pluginInfo = PluginLoader.GetPluginInfo(pluginFolder);

Console.WriteLine($"Plugin Info: {pluginInfo?.Name} {pluginInfo?.Description} {pluginInfo?.Author} {pluginInfo?.Version} {pluginInfo?.Website}");
Console.WriteLine();

var pluginPath = Path.Combine(currentDirectory, pluginFolder, "InfoPanel.Extras.dll");

var plugins = PluginLoader.InitializePlugin(pluginPath);

Dictionary<string, PluginWrapper> loadedPlugins = [];

foreach (var plugin in plugins)
{
    PluginWrapper pluginWrapper = new(Path.GetFileName(pluginPath), plugin);
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



Thread.Sleep(1000000);
Console.Clear();

StringBuilder buffer = new();
string lastOutput = string.Empty;

while (true)
{
    buffer.Clear();

    foreach (var wrapper in loadedPlugins.Values)
    {
        wrapper.Update();

        buffer.AppendLine($"-{wrapper.Name} ({wrapper.Plugin.GetType().FullName}) [UpdateInterval={wrapper.UpdateInterval.TotalMilliseconds}ms, UpdateTime={wrapper.UpdateTimeMilliseconds}ms]");

        foreach (var container in wrapper.PluginContainers)
        {
            buffer.AppendLine($"--{container.Name}");
            foreach (var entry in container.Entries)
            {
                var id = $"/{wrapper.Id}/{container.Id}/{entry.Id}";

                if(entry is IPluginText text)
                {
                    buffer.AppendLine($"---{text.Name}: {text.Value}");
                }else if(entry is IPluginSensor sensor)
                {
                    buffer.AppendLine($"---{sensor.Name}: {sensor.Value}{sensor.Unit}");
                }
            }
        }

        buffer.AppendLine();
    }

    // Only update the console if the output has changed with double buffering to reduce flicker
    var output = buffer.ToString();
    if (output != lastOutput)
    {
        lastOutput = output;
        Console.Clear();
        Console.WriteLine(output);
    }

    Thread.Sleep(30);
}

