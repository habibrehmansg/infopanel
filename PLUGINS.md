# InfoPanel Plugin Development Guide

## Table of Contents
- [Introduction](#introduction)
- [InfoPanel 1.x and 2.0](#infopanel-1x-and-20)
- [Quick Start](#quick-start)
- [Plugin Components](#plugin-components)
  - [Base Plugin](#base-plugin)
  - [Plugin Containers](#plugin-containers)
  - [Plugin Data Types](#plugin-data-types)
- [Image Providers](#image-providers)
  - [SkiaSharp 4](#skiasharp-4)
- [Plugin Actions](#plugin-actions)
- [Plugin Configuration](#plugin-configuration)
  - [Automatic Config Persistence](#automatic-config-persistence)
- [Plugin Lifecycle](#plugin-lifecycle)
- [Examples](#examples)
  - [Simple Sensor Plugin](#simple-sensor-plugin)
  - [Table Data Plugin](#table-data-plugin)
- [Building and Deployment](#building-and-deployment)
- [Debugging](#debugging)
- [Best Practices](#best-practices)
- [Panel Drivers](#panel-drivers) (2.0, .NET 10)

## Introduction

InfoPanel features a plugin system that enables developers to extend its capabilities with custom data sources, visualizations, and images. Plugins can provide sensors, text values, tables, and rendered images that are displayed in InfoPanel's overlays and panels.

Each plugin runs in an isolated host process, communicating with the main application over named pipes. This ensures that a misbehaving plugin cannot crash InfoPanel. For details on how the host system works internally, see [PLUGIN-ARCHITECTURE.md](PLUGIN-ARCHITECTURE.md).

## InfoPanel 1.x and 2.0

This guide was written for InfoPanel 1.x, the Windows app (branch `1.4.x`). The SDK (`InfoPanel.Plugins`, .NET 8) is the same in 2.0, whose engine runs 1.x plugins as they are, on Windows and macOS; on macOS it skips plugin types marked for Windows only, and a plugin type that provides [images](#image-providers) doesn't start there at all (its image channel needs Windows' shared memory), so its sensors don't show either. What 2.0 does differently ([docs/2.0/plugins.md](docs/2.0/plugins.md)):

- **What the engine reads:** sensors, texts and tables, under 1.x's keys. It doesn't show [images](#image-providers), offer [actions](#plugin-actions) or let a user change a plugin's [settings](#plugin-configuration) yet.
- **Where plugins go:** a package is a folder `InfoPanel.{Name}` holding `InfoPanel.{Name}.dll`. The engine reads its bundled plugins from `plugins/` beside itself and the user's from `plugins/` in its data folder (`%LOCALAPPDATA%\InfoPanel\v2\plugins\` on Windows, `~/Library/Application Support/InfoPanel/v2/plugins/` on macOS), when it starts. There is no plugin browser and no "Add Plugin from ZIP" yet: copy the folder there and start the engine again.
- **Settings:** stored in the data folder, `plugin-settings/{package}/{plugin id}.json`, and applied when the plugin starts. 2.0 never writes to 1.x's folders.
- **Logs:** `logs/plugins/{package}-{yyyyMMdd}.log` in the data folder.
- **The host:** the engine's own executable, started as `InfoPanel --plugin-host` ([PLUGIN-ARCHITECTURE.md](PLUGIN-ARCHITECTURE.md)).

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

For sensor/text/table plugins, reference the core package. InfoPanel provides it at run time, so don't copy it into your output (`Private` false, runtime assets excluded):

```xml
<ItemGroup>
  <ProjectReference Include="..\InfoPanel.Plugins\InfoPanel.Plugins.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
</ItemGroup>
```

If your plugin provides images, also reference the graphics package, in the same way:

```xml
<ItemGroup>
  <ProjectReference Include="..\InfoPanel.Plugins\InfoPanel.Plugins.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
  <ProjectReference Include="..\InfoPanel.Plugins.Graphics\InfoPanel.Plugins.Graphics.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
  </ProjectReference>
</ItemGroup>
```

A copy of either DLL left in your plugin's folder is ignored: the plugin always runs against InfoPanel's own.

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

    using var font = new SKFont(SKTypeface.Default, 24);
    using var paint = new SKPaint
    {
        Color = SKColors.White,
        IsAntialias = true
    };
    canvas.DrawText("Hello from plugin!", 10, 40, SKTextAlign.Left, font, paint);

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

### SkiaSharp 4

The 2.0 engine provides SkiaSharp **4.155.0** to its plugins. The plugin host always loads the engine's SkiaSharp (a copy in your plugin's folder is ignored), so `IPluginImageWriter.Bitmap` is the same `SKBitmap` type on both sides. A plugin that draws must be rebuilt against SkiaSharp 4.155.0:

- A plugin compiled against SkiaSharp 3 still loads, but a call to an API that SkiaSharp 4 removed or made private fails when that code runs, with `MissingMethodException` or `MethodAccessException`. For example, an `SKBitmap` subclass that sets `IgnorePublicDispose` (protected in 3.x, private in 4.x) throws `MethodAccessException`.
- `SKPaint`'s text members (`TextSize`, `Typeface`, `DrawText` with only a paint) are obsolete: draw text with an `SKFont` that names its typeface, as in the example above (a new `SKFont`'s default typeface is empty in SkiaSharp 4).
- `SKTypeface.FromFamilyName` for a family that isn't installed returns `SKTypeface.Default` in its regular style, not the default family in the weight and slant asked for.

SkiaSharp's [4.x release notes](https://mono.github.io/SkiaSharp/docs/releases/4.148.0.html) and [API diff](https://mono.github.io/SkiaSharp/docs/releases/4.148.0/SkiaSharp/SkiaSharp.html) list every change.

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

Config values are automatically persisted by the host process. In 1.x, when a user changes a config value in the UI, the host saves all current `ConfigProperties` values to a JSON file under `%LOCALAPPDATA%\InfoPanel\plugins\` (e.g. `my-plugin-id.config.json`). On next startup, stored values are automatically restored via `ApplyConfig()` after `Initialize()` returns. 2.0's host keeps them in its data folder instead ([above](#infopanel-1x-and-20)).

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
   - **External plugins:** `%ProgramData%\InfoPanel\plugins\YourPluginName\` (1.x)
   - **2.0:** `plugins\InfoPanel.YourPluginName\` in the engine's data folder ([above](#infopanel-1x-and-20))
3. The folder must follow the convention: `YourPluginName/YourPluginName.dll` (2.0: `InfoPanel.YourPluginName/InfoPanel.YourPluginName.dll`)

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

In 1.x, users can install by:
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

## Panel Drivers

InfoPanel 2.0 drives USB panels through **panel drivers**, a kind of plugin of their own: everything above is about sensor plugins (.NET 8). A panel driver targets **.NET 10**, the engine's runtime, and references `InfoPanel.Panels.Sdk` (in this repository). One project can't be both, and a package holds one or the other. What the engine does with a driver: [docs/2.0/panels.md](docs/2.0/panels.md); how it finds, runs and bounds a panel package: [docs/2.0/plugins.md](docs/2.0/plugins.md#panel-packages).

A driver is two small interfaces:

- `IPanelDriver`: its `Family` id, the USB devices it `Matches` (vendor and product ids, with a transport), a `Probe` that tells one of its own panels (it may open the device and ask it, never draws) and answers its identity (model, size, serial, firmware), and `Open`, which runs the start sequence and answers an `IPanelSession`.
- `IPanelSession`: its `Capabilities` (size, pixel formats in order of preference, the most frames a second, a JPEG cap and quality, brightness range), `WriteFrame` (one frame, answering the time the transfer alone took: the engine paces the panel by it), `SetBrightness`, and `Close`, which turns the screen off within the budget it is given.

The engine renders, turns, encodes and paces every frame, keeps each panel's settings and serves the API: a driver only talks to its device, and never retries (a failed call ends the session; the engine opens it again a second later). A driver has a public constructor that takes nothing.

### The package

A panel package is a folder `InfoPanel.{Name}` with `InfoPanel.{Name}.dll` (the driver, built with the SDK referenced but not copied: `<ProjectReference … Private="false" />`) and a `panel.json` beside it:

```json
{ "format": "infopanel.panel-package/1", "sdk": "1.0",
  "families": [{ "id": "virtual", "name": "Virtual panel",
    "match": [{ "usb": "1209:0001" }],
    "models": [{ "id": "480x320", "name": "Virtual panel 480 × 320", "width": 480, "height": 320 }] }] }
```

The engine reads `panel.json` when it starts, so a family is known before its driver runs; `sdk` is the panel SDK version the package was built for (the engine runs its own major and a minor no higher), and a family id is unique across every package. Each package runs in a panel host, a process of its own (`InfoPanel --panel-host`), started when one of its devices is plugged in: a driver that crashes takes down only its own panels, and is started again. `InfoPanel.Panels.Virtual` in this repository is a complete package (a virtual panel with no device behind it, for tests), and its `panel.json` the shape to copy. Copy a package into the data folder's `plugins/` and start the engine again.
