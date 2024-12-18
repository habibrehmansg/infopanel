using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;

PluginLoader pluginLoader = new();

//\InfoPanel\InfoPanel.Plugins.Simulator\bin\Debug\net8.0-windows
var plugins = pluginLoader.InitializePlugin("..\\..\\..\\..\\InfoPanel.VolumePlugin\\bin\\x64\\Debug\\net8.0-windows\\InfoPanel.Extras.dll");

List<IPlugin> loadedPlugins = [];

foreach (var plugin in plugins)
{
    loadedPlugins.Add(plugin);
    plugin.Initialize(); 
}

new Task(async () =>
{
    while (true)
    {
        foreach (var plugin in loadedPlugins)
        {
            await plugin.UpdateAsync();
        }
        await Task.Delay(300);
    }
}).Start();

while (true)
{
    Console.Clear();

    foreach (var plugin in loadedPlugins)
    {
        Console.Write("-");
        Console.WriteLine($"{plugin.Name} ({plugin.GetType().FullName})");
        var panelDatas = plugin.GetData();

        foreach (var panelData in panelDatas)
        {
            Console.Write("--");
            Console.WriteLine($"{panelData.Name}: {panelData.Value}{panelData.Unit}");
        }

        Console.WriteLine();
    }

    Thread.Sleep(10);
}

