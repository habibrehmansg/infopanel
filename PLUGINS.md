# InfoPanel Plugin Development Guide

## Table of Contents
- [Introduction](#introduction)
- [Quick Start](#quick-start)
- [Plugin Components](#plugin-components)
  - [Base Plugin](#base-plugin)
  - [Plugin Containers](#plugin-containers)
  - [Plugin Data Types](#plugin-data-types)
- [Image Providers](#image-providers)
- [Plugin Actions](#plugin-actions)
- [Plugin Configuration](#plugin-configuration)
- [Plugin Lifecycle](#plugin-lifecycle)
- [Configuration Files](#configuration-files)
- [Examples](#examples)
  - [Simple Sensor Plugin](#simple-sensor-plugin)
  - [Table Data Plugin](#table-data-plugin)
- [Building and Deployment](#building-and-deployment)
- [Debugging](#debugging)
- [Best Practices](#best-practices)

## Introduction

InfoPanel features a plugin system that enables developers to extend its capabilities with custom data sources, visualizations, and images. Plugins can provide sensors, text values, tables, and rendered images that are displayed in InfoPanel's overlays and panels.

Each plugin runs in an isolated host process, communicating with the main application over named pipes. This ensures that a misbehaving plugin cannot crash InfoPanel. For details on how the host system works internally, see [PLUGIN-ARCHITECTURE.md](PLUGIN-ARCHITECTURE.md).

## Quick Start

### 1. Create a .NET Class Library

Create a new .NET 8.0 class library project:

```
MyPlugin/
  |- MyPlugin.csproj
  |- MyPlugin.cs
  |- PluginInfo.ini
```

### 2. Add Project References

For sensor/text/table plugins, reference the core package:

```xml
<ItemGroup>
  <ProjectReference Include="..\InfoPanel.Plugins\InfoPanel.Plugins.csproj" />
</ItemGroup>
```

If your plugin provides images, also reference the graphics package:

```xml
<ItemGroup>
  <ProjectReference Include="..\InfoPanel.Plugins\InfoPanel.Plugins.csproj" />
  <ProjectReference Include="..\InfoPanel.Plugins.Graphics\InfoPanel.Plugins.Graphics.csproj" />
</ItemGroup>
```

### 3. Create PluginInfo.ini

Every plugin must include a `PluginInfo.ini` file in its output directory:

```ini
[PluginInfo]
Name=My Plugin
Description=A description of what your plugin does
Author=Your Name
Version=1.0.0
Website=https://yourwebsite.com
```

> **Important:** The section header must be `[PluginInfo]`, not `[Plugin]`.

### 4. Implement Your Plugin

```csharp
using InfoPanel.Plugins;

namespace MyCompany.MyPlugin;

public class MyPlugin : BasePlugin
{
    private readonly PluginSensor _value = new("value", "My Value", 0, "units");

    public MyPlugin() : base("my-plugin", "My Plugin", "A sample plugin") { }

    public override string? ConfigFilePath => null;
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public override void Initialize() { }

    public override void Load(List<IPluginContainer> containers)
    {
        var container = new PluginContainer("main", "Main Data");
        container.Entries.Add(_value);
        containers.Add(container);
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        _value.Value = 42.0f;
        return Task.CompletedTask;
    }

    public override void Update() => throw new NotImplementedException();
    public override void Close() { }
}
```

## Plugin Components

### Base Plugin

Your main plugin class must inherit from `BasePlugin` and implement all required members:

```csharp
public class MyPlugin : BasePlugin
{
    // Constructor: provide a unique ID, display name, and description
    public MyPlugin() : base("my-plugin", "My Plugin", "Description") { }

    // Path to a config file, or null if not needed
    public override string? ConfigFilePath => null;

    // How often UpdateAsync is called
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    // Called once after loading
    public override void Initialize() { }

    // Register containers and data items
    public override void Load(List<IPluginContainer> containers) { }

    // Periodic async update — update your sensor values here
    public override Task UpdateAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    // Synchronous update (alternative to UpdateAsync)
    public override void Update() => throw new NotImplementedException();

    // Clean up resources on shutdown
    public override void Close() { }
}
```

> **Note:** `BasePlugin` also has a constructor overload `BasePlugin(string name, string description = "")` that auto-generates the ID from the name.

### Plugin Containers

Containers group related data items. Each container appears as a section in the InfoPanel UI:

```csharp
var container = new PluginContainer("container-id", "Container Name");
container.Entries.Add(mySensor);
container.Entries.Add(myText);
containers.Add(container);
```

The `isEphemeralPath` parameter (default `false`) can be set to `true` for containers whose entries change dynamically between updates.

### Plugin Data Types

#### PluginText

Simple text values:

```csharp
var text = new PluginText("text-id", "Text Name", "Initial value");
container.Entries.Add(text);

// Update later:
text.Value = "New value";
```

#### PluginSensor

Numeric sensor values with optional units. Automatically tracks min, max, and rolling average (60 samples):

```csharp
var sensor = new PluginSensor("sensor-id", "Sensor Name", 0f, "°C");
container.Entries.Add(sensor);

// Update later:
sensor.Value = 65.5f;  // Min, Max, Avg are computed automatically
```

#### PluginTable

Tabular data displayed in InfoPanel's table view:

```csharp
var dataTable = new DataTable();
dataTable.Columns.Add("Process", typeof(PluginText));
dataTable.Columns.Add("CPU", typeof(PluginSensor));
dataTable.Columns.Add("Memory", typeof(PluginSensor));

var row = dataTable.NewRow();
row[0] = new PluginText("name", "chrome.exe");
row[1] = new PluginSensor("cpu", 12.5f, "%");
row[2] = new PluginSensor("memory", 450f, " MB");
dataTable.Rows.Add(row);

// Format string: "columnIndex:width|columnIndex:width"
var table = new PluginTable("table-id", "Table Name", dataTable, "0:150|1:60|2:80");
container.Entries.Add(table);
```

## Image Providers

Plugins can render custom images that appear as display items in InfoPanel. To provide images, implement `IPluginImageProvider` alongside `BasePlugin`, and reference `InfoPanel.Plugins.Graphics`.

### Declaring Image Outputs

Return descriptors for each image your plugin produces:

```csharp
using InfoPanel.Plugins;
using InfoPanel.Plugins.Graphics;

public class MyImagePlugin : BasePlugin, IPluginImageProvider
{
    public IReadOnlyList<PluginImageDescriptor> ImageDescriptors { get; } =
    [
        new PluginImageDescriptor("main-image", "Main Display", 400, 200)
    ];

    private IPluginImageWriter? _writer;

    public void OnImageBuffersReady(IReadOnlyDictionary<string, IPluginImageWriter> writers)
    {
        // Called after Load() — store writer references for use in UpdateAsync
        _writer = writers["main-image"];
    }

    // ... rest of BasePlugin implementation
}
```

### Drawing with IPluginImageWriter

The `IPluginImageWriter` gives you an `SKBitmap` to draw on. Call `Invalidate()` after drawing to publish the frame:

```csharp
public override Task UpdateAsync(CancellationToken cancellationToken)
{
    if (_writer is null) return Task.CompletedTask;

    using var canvas = new SKCanvas(_writer.Bitmap);
    canvas.Clear(SKColors.Black);

    using var paint = new SKPaint
    {
        Color = SKColors.White,
        TextSize = 24,
        IsAntialias = true
    };
    canvas.DrawText("Hello from plugin!", 10, 40, paint);

    _writer.Invalidate(); // Publish the frame

    return Task.CompletedTask;
}
```

### IPluginImageWriter API

| Member | Description |
|---|---|
| `SKBitmap Bitmap` | The SkiaSharp bitmap to draw on |
| `int Width` | Current image width |
| `int Height` | Current image height |
| `void Invalidate()` | Publish the current frame to InfoPanel |
| `void Resize(int width, int height)` | Request a new image size (recreates the backing buffer) |

> **Note:** Images are backed by memory-mapped files with double buffering. `Invalidate()` atomically swaps the active buffer, so drawing and reading never conflict.

## Plugin Actions

Plugins can expose actions that appear as buttons in the InfoPanel UI. Decorate methods with `[PluginAction]`:

```csharp
public class MyPlugin : BasePlugin
{
    [PluginAction(DisplayName = "Reset Counters")]
    public void ResetCounters()
    {
        _counter.Value = 0;
        _total.Value = 0;
    }

    [PluginAction(DisplayName = "Refresh Data")]
    public void RefreshData()
    {
        // Force an immediate data refresh
    }

    // ... rest of plugin
}
```

Action methods must be `public`, take no parameters, and return `void`. The `DisplayName` is shown in the UI.

## Plugin Configuration

Plugins can expose configurable properties that appear in the InfoPanel settings UI. Implement `IPluginConfigurable`:

```csharp
public class MyPlugin : BasePlugin, IPluginConfigurable
{
    private string _apiKey = "";
    private int _refreshInterval = 30;
    private bool _showDetails = true;

    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new PluginConfigProperty
        {
            Key = "apiKey",
            DisplayName = "API Key",
            Type = PluginConfigType.String,
            Value = _apiKey
        },
        new PluginConfigProperty
        {
            Key = "refreshInterval",
            DisplayName = "Refresh Interval (seconds)",
            Type = PluginConfigType.Integer,
            Value = _refreshInterval,
            MinValue = 5,
            MaxValue = 300,
            Step = 5
        },
        new PluginConfigProperty
        {
            Key = "showDetails",
            DisplayName = "Show Detailed Info",
            Type = PluginConfigType.Boolean,
            Value = _showDetails
        },
        new PluginConfigProperty
        {
            Key = "unit",
            DisplayName = "Temperature Unit",
            Type = PluginConfigType.Choice,
            Value = "Celsius",
            Options = ["Celsius", "Fahrenheit"]
        }
    ];

    public void ApplyConfig(string key, object? value)
    {
        switch (key)
        {
            case "apiKey":
                _apiKey = value as string ?? "";
                break;
            case "refreshInterval":
                _refreshInterval = Convert.ToInt32(value);
                break;
            case "showDetails":
                _showDetails = Convert.ToBoolean(value);
                break;
            case "unit":
                // Handle unit change
                break;
        }
    }

    // ... rest of plugin
}
```

### Configuration Types

| Type | C# Type | UI Control | Extra Properties |
|---|---|---|---|
| `String` | `string` | Text box | — |
| `Integer` | `int` | Numeric input | `MinValue`, `MaxValue`, `Step` |
| `Double` | `double` | Numeric input | `MinValue`, `MaxValue`, `Step` |
| `Boolean` | `bool` | Toggle switch | — |
| `Choice` | `string` | Dropdown | `Options` (string array) |

### Automatic Config Persistence

Config values are automatically persisted by the host process. When a user changes a config value in the UI, the host saves all current `ConfigProperties` values to a JSON file under `%LOCALAPPDATA%\InfoPanel\plugins\` (e.g. `my-plugin-id.config.json`). On next startup, stored values are automatically restored via `ApplyConfig()` after `Initialize()` returns.

**You do not need to implement any file I/O** — just keep your config state in memory and the host handles persistence transparently.

## Plugin Lifecycle

1. **Discovery**: InfoPanel scans plugin directories for folders containing `{FolderName}/{FolderName}.dll`
2. **Host Launch**: A separate host process is spawned for the plugin, connected via named pipe
3. **Loading**: The plugin assembly is loaded in an isolated `AssemblyLoadContext`
4. **Initialization**: `Initialize()` is called once
5. **Config Restore**: If the plugin implements `IPluginConfigurable`, stored config values are loaded and applied via `ApplyConfig()` calls
6. **Container Setup**: `Load()` is called — register all containers and data items here
7. **Image Setup**: If `IPluginImageProvider` is implemented, `OnImageBuffersReady()` is called with writers
8. **Updates**: `UpdateAsync()` is called periodically according to `UpdateInterval`
9. **Shutdown**: `Close()` is called when InfoPanel exits or the plugin is disabled

## Examples

### Simple Sensor Plugin

A plugin that provides system uptime information:

```csharp
public class UptimePlugin : BasePlugin
{
    private readonly PluginText _uptimeFormatted = new("formatted", "Formatted Uptime", "-");
    private readonly PluginSensor _uptimeDays = new("days", "Days", 0);
    private readonly PluginSensor _uptimeHours = new("hours", "Hours", 0);
    private readonly PluginSensor _uptimeMinutes = new("minutes", "Minutes", 0);

    public UptimePlugin() : base("uptime-plugin", "System Uptime", "Provides system uptime information") { }

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

A plugin that provides process information in table format:

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
                row[1] = new PluginSensor("cpu", 0, "%");
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

1. Build your plugin project in Release mode
2. Copy the output folder (DLL, dependencies, and `PluginInfo.ini`) to:
   - **External plugins:** `%ProgramData%\InfoPanel\plugins\YourPluginName\`
3. The folder must follow the convention: `YourPluginName/YourPluginName.dll`

### Listing in the Plugin Browser

To make your plugin discoverable inside InfoPanel, add it to the community plugin registry. See the [Plugin Registry Guide](PLUGIN-REGISTRY.md) for instructions.

### Distributing Your Plugin

Create a ZIP file with this structure:

```
YourPluginName.zip
└── YourPluginName/
    ├── YourPluginName.dll
    ├── [dependency DLLs]
    └── PluginInfo.ini
```

Users can install by:
- Extracting the ZIP to `%ProgramData%\InfoPanel\plugins\`
- Or using the "Add Plugin from ZIP" option in InfoPanel's Plugins page

## Debugging

Use the Plugin Simulator to test your plugin without launching the full InfoPanel application:

1. Reference your plugin project from `InfoPanel.Plugins.Simulator`
2. Modify the simulator's `Program.cs` to load your plugin
3. Set the simulator as the startup project
4. Run in debug mode

```bash
dotnet run --project InfoPanel.Plugins.Simulator/InfoPanel.Plugins.Simulator.csproj
```

## Best Practices

1. **Use meaningful IDs**: Plugin and container IDs should be stable across versions — they're used for data binding
2. **Handle exceptions**: Wrap potentially problematic code in try-catch blocks — unhandled exceptions in `UpdateAsync` will be reported but won't crash the host
3. **Clean up resources**: Dispose of resources in `Close()` — the host process will terminate, but clean shutdown is preferred
4. **Optimize updates**: Keep `UpdateAsync()` fast — long-running updates block the next cycle
5. **Thread safety**: `UpdateAsync()` runs on a single thread, but image writers may be read concurrently — always call `Invalidate()` after completing a frame
6. **Minimize allocations**: Reuse data objects where possible; avoid allocating new `PluginSensor`/`PluginText` instances every update
7. **Use delta-friendly data**: The host only transmits changed values — keep sensor values stable when unchanged to reduce IPC overhead

---

For questions or help with plugin development, join the [InfoPanel Discord][discord] community.

[discord]: https://discord.gg/aNGeJxjE7Q
