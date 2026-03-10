# InfoPanel Plugin Development Guide

## Table of Contents
- [Introduction](#introduction)
- [Plugin Architecture](#plugin-architecture)
- [Creating Your First Plugin](#creating-your-first-plugin)
- [Plugin Components](#plugin-components)
  - [Base Plugin](#base-plugin)
  - [Plugin Containers](#plugin-containers)
  - [Plugin Data Types](#plugin-data-types)
- [Plugin Lifecycle](#plugin-lifecycle)
- [Configuration](#configuration)
- [Examples](#examples)
  - [Simple Sensor Plugin](#simple-sensor-plugin)
  - [Table Data Plugin](#table-data-plugin)
- [Building and Deployment](#building-and-deployment)
- [Debugging Plugins](#debugging-plugins)
- [Best Practices](#best-practices)

## Introduction

InfoPanel features a robust plugin system that enables developers to extend its capabilities with custom data sources and visualizations. Plugins can provide various types of data, including sensors, text values, and even complex data tables that can be displayed in InfoPanel's interface.

This guide will walk you through the process of creating, testing, and deploying plugins for InfoPanel.

## Plugin Architecture

The InfoPanel plugin system is based on a clean, modular architecture with the following key components:

1. **Plugin Interface** (`IPlugin`): The core interface that all plugins must implement
2. **Plugin Loader**: Handles dynamic loading of plugin assemblies
3. **Plugin Containers**: Group related data items within a plugin
4. **Plugin Data Types**: Various data types that plugins can provide (sensors, text, tables)

Plugins are loaded as separate assemblies and run in their own context, ensuring stability and isolation from the main application.

## Creating Your First Plugin

To create a new InfoPanel plugin:

1. Create a new .NET class library project targeting .NET 8.0
2. Add references to the InfoPanel.Plugins package or DLL
3. Create a main plugin class that inherits from `BasePlugin`
4. Implement the required methods
5. Compile and deploy your plugin

### Required Project Structure

```
MyCustomPlugin/
  |- MyCustomPlugin.csproj  (Target .NET 8.0)
  |- MyCustomPlugin.cs      (Main plugin class)
  |- PluginInfo.ini         (Plugin metadata)
  |- [Other code files]
```

### Plugin Metadata (PluginInfo.ini)

Every plugin should have a `PluginInfo.ini` file with the following format:

```ini
[Plugin]
Name=My Custom Plugin
Description=A description of what your plugin does
Author=Your Name
Version=1.0.0
Website=https://yourwebsite.com
```

## Plugin Components

### Base Plugin

Your main plugin class should inherit from `BasePlugin` and implement all required methods:

```csharp
using InfoPanel.Plugins;
using System.Threading;
using System.Threading.Tasks;

namespace MyCompany.MyCustomPlugin
{
    public class MyCustomPlugin : BasePlugin
    {
        public MyCustomPlugin() : base("my-custom-plugin", "My Custom Plugin", "Description of my plugin")
        {
        }

        public override string? ConfigFilePath => null; // Path to plugin config file if needed
        
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1); // How often the plugin should update

        public override void Initialize()
        {
            // Initialize your plugin, set up resources
        }

        public override void Load(List<IPluginContainer> containers)
        {
            // Create containers and add data items
            var container = new PluginContainer("main", "Main Data");
            container.Entries.Add(new PluginText("greeting", "Greeting", "Hello World"));
            container.Entries.Add(new PluginSensor("sample-value", "Sample Value", 42.5f, "units"));
            containers.Add(container);
        }

        public override void Update()
        {
            // Synchronous update method (optional implementation)
            throw new NotImplementedException();
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            // Asynchronous update method - update your sensor values here
            return Task.CompletedTask;
        }

        public override void Close()
        {
            // Clean up resources
        }
    }
}
```

### Plugin Containers

Containers group related data items within your plugin:

```csharp
var container = new PluginContainer("container-id", "Container Display Name");
// Add data items to the container
containers.Add(container);
```

### Plugin Data Types

InfoPanel supports several data types that plugins can provide:

#### Plugin Text

Simple text values:

```csharp
var textValue = new PluginText("text-id", "Text Name", "Initial value");
container.Entries.Add(textValue);
```

#### Plugin Sensor

Numeric sensor values with optional units:

```csharp
var sensor = new PluginSensor("sensor-id", "Sensor Name", 42.0f, "units");
container.Entries.Add(sensor);
```

#### Plugin Table

Complex tabular data:

```csharp
var table = new DataTable();
table.Columns.Add("Process Name", typeof(PluginText));
table.Columns.Add("CPU Usage", typeof(PluginSensor));

var row = table.NewRow();
row[0] = new PluginText("Process Name", "chrome.exe");
row[1] = new PluginSensor("CPU Usage", 12.5f, "%");
table.Rows.Add(row);

var tableData = new PluginTable("table-id", "Table Name", table, "0:100|1:80");
container.Entries.Add(tableData);
```

## Plugin Lifecycle

1. **Loading**: The plugin assembly is loaded by InfoPanel
2. **Initialization**: `Initialize()` is called once when the plugin is first loaded
3. **Container Setup**: `Load()` is called to set up containers and initial data items
4. **Updates**: `UpdateAsync()` is called periodically according to the `UpdateInterval` property
5. **Closing**: `Close()` is called when InfoPanel is shutting down or the plugin is being disabled

## Configuration

If your plugin requires configuration, you can specify a configuration file path:

```csharp
public override string? ConfigFilePath => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "config.ini");
```

You can then use the `Config` class to load and save configuration:

```csharp
// Initialize configuration
Config.Instance.Load();

// Get a configuration value
if (Config.Instance.TryGetValue("Section", "Key", out var value))
{
    // Use value
}

// Save a configuration value
Config.Instance.SetValue("Section", "Key", "Value");
Config.Instance.Save();
```

## Examples

### Simple Sensor Plugin

Here's an example of a plugin that provides system uptime information:

```csharp
public class UptimePlugin : BasePlugin
{
    private readonly PluginText _uptimeFormatted = new("formatted", "Formatted Uptime", "-");
    private readonly PluginSensor _uptimeDays = new("days", "Days", 0);
    private readonly PluginSensor _uptimeHours = new("hours", "Hours", 0);
    private readonly PluginSensor _uptimeMinutes = new("minutes", "Minutes", 0);
    
    public UptimePlugin() : base("uptime-plugin", "System Uptime", "Provides system uptime information")
    {
    }

    public override string? ConfigFilePath => null;
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public override void Initialize() { }

    public override void Load(List<IPluginContainer> containers)
    {
        var container = new PluginContainer("uptime", "System Uptime");
        container.Entries.AddRange([_uptimeFormatted, _uptimeDays, _uptimeHours, _uptimeMinutes]);
        containers.Add(container);
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        long uptimeMilliseconds = Environment.TickCount64;
        TimeSpan uptime = TimeSpan.FromMilliseconds(uptimeMilliseconds);
        
        _uptimeFormatted.Value = $"{uptime.Days}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
        _uptimeDays.Value = uptime.Days;
        _uptimeHours.Value = uptime.Hours;
        _uptimeMinutes.Value = uptime.Minutes;
        
        return Task.CompletedTask;
    }

    public override void Update() => throw new NotImplementedException();
    public override void Close() { }
}
```

### Table Data Plugin

Here's an example of a plugin that provides process information in table format:

```csharp
public class ProcessInfoPlugin : BasePlugin
{
    private readonly PluginTable _processTable;
    
    public ProcessInfoPlugin() : base("process-info", "Process Information", "Shows top processes by CPU usage")
    {
        _processTable = new PluginTable("processes", "Top Processes", new DataTable(), "0:150|1:60|2:80");
    }

    public override string? ConfigFilePath => null;
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public override void Initialize() { }

    public override void Load(List<IPluginContainer> containers)
    {
        var container = new PluginContainer("processes", "Process Information");
        container.Entries.Add(_processTable);
        containers.Add(container);
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        var dataTable = new DataTable();
        dataTable.Columns.Add("Process", typeof(PluginText));
        dataTable.Columns.Add("CPU", typeof(PluginSensor));
        dataTable.Columns.Add("Memory", typeof(PluginSensor));

        var processes = Process.GetProcesses()
            .OrderByDescending(p => {
                try { return p.TotalProcessorTime.TotalMilliseconds; }
                catch { return 0; }
            })
            .Take(10);

        foreach (var process in processes)
        {
            try
            {
                var row = dataTable.NewRow();
                row[0] = new PluginText("name", process.ProcessName);
                row[1] = new PluginSensor("cpu", 0, "%"); // You'd need to calculate real CPU usage
                row[2] = new PluginSensor("memory", process.WorkingSet64 / (1024 * 1024), " MB");
                dataTable.Rows.Add(row);
            }
            catch { }
        }

        _processTable.Value = dataTable;
        return Task.CompletedTask;
    }

    public override void Update() => throw new NotImplementedException();
    public override void Close() { }
}
```

## Building and Deployment

Once you've built your plugin, you need to deploy it to the InfoPanel plugins directory:

1. Build your plugin project
2. Copy the output DLL and any dependencies to one of the following locations:
   - For user plugins: `%APPDATA%\Roaming\InfoPanel\Plugins\YourPluginName\`
   - For development: `[InfoPanel Directory]\Plugins\YourPluginName\`

Make sure to include your `PluginInfo.ini` file in the same directory.

## Distributing Your Plugin

To share your plugin with other InfoPanel users:

1. Compile your plugin in Release mode
2. Create a ZIP file with the following structure:
   ```
   YourPluginName.zip
   └── YourPluginName/
       ├── YourPlugin.dll
       ├── [Any dependency DLLs]
       └── PluginInfo.ini
   ```

3. The folder name inside the ZIP must match the ZIP filename (without the .zip extension)
4. Users can install your plugin by:
   - Extracting the ZIP to `%APPDATA%\Roaming\InfoPanel\Plugins\`
   - Or using the "Add Plugin from ZIP" option in InfoPanel's Plugins page

### Publishing Your Plugin

If you'd like your plugin to be featured in the official InfoPanel documentation:

1. Host your plugin on a public GitHub repository
2. Ensure your repository includes:
   - Source code
   - Compiled releases
   - Clear documentation and usage instructions
3. Submit a pull request to the [InfoPanel repository](https://github.com/habibrehmansg/infopanel) to add your plugin to the "Community Plugins" section of the README.md

## Debugging Plugins

To debug your plugin:

1. Set up a reference to your plugin project in the InfoPanel.Plugins.Simulator project
2. Modify the simulator's `Program.cs` to load and test your plugin
3. Set the simulator as the startup project
4. Run in debug mode

This allows you to test your plugin's functionality without launching the full InfoPanel application.

## Best Practices

1. **Provide Meaningful Names**: Use clear, descriptive names for your plugin, containers, and data items
2. **Handle Exceptions**: Wrap potentially problematic code in try-catch blocks
3. **Clean Up Resources**: Properly dispose of any resources in the `Close()` method
4. **Optimize Updates**: Keep your `UpdateAsync()` method efficient
5. **Thread Safety**: Ensure your plugin is thread-safe, especially when updating data
6. **Configuration**: Use configuration files for user-configurable settings
7. **Documentation**: Document your plugin's features and requirements
8. **Testing**: Thoroughly test your plugin in isolation before deploying

For examples of well-structured plugins, check out the built-in plugins in the InfoPanel.Extras directory of the InfoPanel repository.

---

Feel free to join the [InfoPanel Discord][discord] community if you have questions or need help with plugin development.

[discord]: https://discord.gg/aNGeJxjE7Q
