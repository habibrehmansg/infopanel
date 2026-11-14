# InfoPanel Plugin Development Guide

## Table of Contents
- [Introduction](#introduction)
- [InfoPanel 1.x and 2.0](#infopanel-1x-and-20)
- [Quick Start](#quick-start)
- [Plugin Components](#plugin-components)
  - [Base Plugin](#base-plugin)
  - [Plugin Containers](#plugin-containers)
  - [Plugin Data Types](#plugin-data-types)
  - [Numbers a Plugin Can't Read](#numbers-a-plugin-cant-read)
  - [Threads](#threads)
- [Logging](#logging)
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

InfoPanel's plugins add data sources of their own: sensors, texts, tables and pictures, which panels and desktop windows show like any other sensor. A plugin is a .NET 10 class library built against InfoPanel's plugin SDK, `InfoPanel.Plugins`.

Each plugin package runs in a host process of its own, which talks to the engine over a named pipe, so a misbehaving plugin can't crash InfoPanel. How the engine finds, runs and bounds plugins: [docs/2.0/plugins.md](docs/2.0/plugins.md); how the hosts work inside: [PLUGIN-ARCHITECTURE.md](PLUGIN-ARCHITECTURE.md).

## InfoPanel 1.x and 2.0

This guide is for InfoPanel 2.0, whose engine runs on Windows, macOS and Linux. Its SDK is the SDK 1.x plugins were built with, on .NET 10, with additions only (`PluginImage`, `PluginLog`), so **a 1.x plugin loads as it is**: it binds the SDK by name, and the host hands it the engine's copy. Rebuilding one for 2.0 is one line, `<TargetFramework>net10.0</TargetFramework>`, and needs the .NET 10 SDK. What 2.0 does ([docs/2.0/plugins.md](docs/2.0/plugins.md)):

- **What the engine reads:** sensors, texts and tables, under 1.x's keys (`plugin:/{plugin id}/{container id}/{entry id}`), and [pictures](#pluginimage) (`PluginImage`, 2.0 only). It doesn't show drawn [images](#image-providers) yet, and on macOS and Linux a plugin type that provides them doesn't start at all (its image channel needs Windows' shared memory). Types marked `[SupportedOSPlatform]` for other operating systems are skipped; a package can say where it runs with `Platforms=` in its [PluginInfo.ini](#3-create-plugininfoini).
- **Actions and settings:** InfoPanel's Plugins page lists a plugin's [actions](#plugin-actions) as buttons and its [settings](#plugin-configuration) by type, through the engine's API.
- **Where plugins go:** a package is a folder `InfoPanel.{Name}` holding `InfoPanel.{Name}.dll`. The engine reads its bundled plugins from `plugins/` beside itself and the user's from `plugins/` in its data folder (`%LOCALAPPDATA%\InfoPanel\v2\plugins\` on Windows, `~/Library/Application Support/InfoPanel/v2/plugins/` on macOS), when it starts: copy the folder there and start the engine again.
- **Settings:** stored in the data folder, `plugin-settings/{package}/{plugin id}.json`, and applied when the plugin starts. 2.0 never writes to 1.x's folders.
- **Logs:** `logs/plugins/{package}-{yyyyMMdd}.log` in the data folder, with your plugin's own lines ([Logging](#logging)).
- **The host:** the engine's own executable, started as `InfoPanel --plugin-host` ([PLUGIN-ARCHITECTURE.md](PLUGIN-ARCHITECTURE.md)).

## Quick Start

### 1. Create a .NET Class Library

Create a .NET 10 class library (the .NET 10 SDK builds it), named as its package folder will be, `InfoPanel.{Name}`:

```
InfoPanel.MyPlugin/
  |- InfoPanel.MyPlugin.csproj
  |- MyPlugin.cs
  |- PluginInfo.ini
```

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <None Update="PluginInfo.ini" CopyToOutputDirectory="Always" />
  </ItemGroup>
</Project>
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
Platforms=windows, macos, linux
Access=Reads usage numbers from the app's logs, never message text
Access=Changes the app's settings when you Connect, with a backup
```

> **Important:** The section header must be `[PluginInfo]`, not `[Plugin]`. `Platforms` is optional (without it the package runs anywhere); a plugin built on a Windows-only library says `Platforms=windows`. `Access` is optional and may be written up to 8 times, one line each (at most 256 characters): what your plugin reads or changes beyond its own folder, in plain words. InfoPanel shows the lines with your plugin; they are declared, not enforced.

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
sensor.Value = 65.5f;      // Min, Max, Avg are computed automatically
sensor.Value = float.NaN;  // Not known now: shows as "–" until the next number
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

#### PluginImage

A picture the plugin holds as encoded bytes (a cover, an icon, a snapshot), InfoPanel 2.0 only: the engine lists it as an image sensor, whose reading names the picture, and a scene draws it through the `plugin` media provider, its reference a text input bound to the sensor ([docs/2.0/plugins.md](docs/2.0/plugins.md#sensors)). 1.x lists no such entry.

```csharp
var cover = new PluginImage("cover", "Cover Art");
container.Entries.Add(cover);

// Update later:
cover.SetImage(pngOrJpegBytes);   // copied; a PNG, JPEG, GIF or WebP of at most PluginImage.MaxBytes (8 MiB)
cover.Clear();                    // no picture: the sensor has no reading
```

Set a new picture only when it changes: the host sends one only when its bytes changed, at most one a second per entry, and holds all of a host's pictures to a rate ([docs/2.0/plugins.md](docs/2.0/plugins.md#bounds)), so a picture set many times a second goes only as the newest one. A picture over 8 MiB is left out, as no picture, with one warning, and the engine refuses bytes that aren't one of the four formats within the media limits.

### Numbers a Plugin Can't Read

When your plugin doesn't know a number (a device not answering, a file not there yet), set its sensor's value to `float.NaN`, never a made-up 0: InfoPanel shows that sensor as unavailable ("–") while your plugin's other sensors stay live, and reads it again with the next number. An infinity reads the same. `PluginSensor` leaves values that aren't finite out of its minimum, maximum and average, so one unknown reading doesn't spoil them. A sensor can start that way: `new PluginSensor("id", "Name", float.NaN, "%")`. 1.x showed such a number as 0.

### Threads

Set values from any thread: your own timer, an event handler, a background reader. The host reads every entry every 100 ms and sends what changed, so a value set at any time is on its way within 100 ms; `UpdateInterval` only says how often the host calls `UpdateAsync` (or, at zero or less, `Update` every 100 ms). `PluginSensor` is safe to set from several threads at once, and the host reads each value with the minimum, maximum and average set with it; for texts and tables, set a whole new value (a new `DataTable`) rather than changing the one you set before. Actions and setting changes are called on other threads than your updates: guard what they share.

## Logging

`PluginLog` writes to your package's log, `logs/plugins/{package}-{yyyyMMdd}.log` in the engine's data folder, at the engine's log level (a Debug line is kept only while the engine logs at Debug):

```csharp
PluginLog.Info("Connected to the device");
PluginLog.Debug($"Read {count} values in {elapsed.TotalMilliseconds} ms");
PluginLog.Warning("The device sent a reading the plugin doesn't know");
PluginLog.Error("Reading the device failed", exception);
```

A message is cut at 4,096 characters, and a failing log never fails your plugin. Outside InfoPanel (the simulator, your own tests) the lines go nowhere. Never log a secret (a setting's value, a token) or your user's data. What your plugin writes to standard output is dropped, and of standard error only the end is kept, to explain a host that failed.

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
    [PluginAction("Reset Counters")]
    public void ResetCounters()
    {
        _counter.Value = 0;
        _total.Value = 0;
    }

    [PluginAction("Refresh Data")]
    public void RefreshData()
    {
        // Force an immediate data refresh
    }

    // ... rest of plugin
}
```

Action methods must be `public`, take no parameters, and return `void` (or a `Task`, which runs until it completes). The display name is shown in the UI. An action runs on a thread of its own, so a long one holds neither your updates nor your other actions; the Plugins page shows it running until it returns. An action that throws is reported as the plugin's error, its message shown with it.

### Action States and Confirmation (2.0)

An action that changes something outside InfoPanel can say, before it runs, whether it can run now, one line of its state, and what a person confirms first. Implement `IPluginActionStates`:

```csharp
public class MyPlugin : BasePlugin, IPluginActionStates
{
    [PluginAction("Connect")]
    public void Connect() { /* ... */ }

    public PluginActionState? Describe(string method) => method switch
    {
        nameof(Connect) when _connected => new PluginActionState { Enabled = false, State = "Connected" },
        nameof(Connect) => new PluginActionState
        {
            State = "Not connected · 2 folders",
            Confirm = new PluginActionConfirmation
            {
                Title = "Connect?",
                Text = "This changes one settings file, keeping a backup beside it.\n\nDisconnect puts it back.",
            },
        },
        _ => null, // the default: enabled, no state, no confirmation
    };
}
```

- `Enabled`: whether it can run now. InfoPanel asks before every run and never runs a disabled action; its `State` should say why.
- `State`: one line shown with the action (at most 256 characters), or null.
- `Confirm`: a title (empty: the action's name; at most 256 characters) and plain text (no markup; paragraphs separated by blank lines; at most 4,096 characters) the Plugins page shows before it runs the action, or null to run it at once.

`Describe` is called on any thread, while your plugin updates and while the action itself runs: keep it quick and guard what it reads. One that throws, or takes over 5 seconds, makes the action disabled, saying so, and is reported as your plugin's error once until it answers again; while one call for an action hasn't returned, it isn't called again for that action. A plugin without `IPluginActionStates` (every 1.x plugin) has every action enabled, with no state and no confirmation.

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

Config values are automatically persisted by the host process. When a user changes a config value, the host saves all current `ConfigProperties` values to `plugin-settings/{package}/{plugin id}.json` in the engine's data folder; at the next start, stored values are restored through `ApplyConfig()` after `Load()` returns. (1.x kept them under `%LOCALAPPDATA%\InfoPanel\plugins\`.) The engine logs a change by its key, never its value, so a setting may hold a secret such as an API key; don't log it yourself.

**You do not need to implement any file I/O** — just keep your config state in memory and the host handles persistence transparently.

## Plugin Lifecycle

1. **Discovery**: when it starts, the engine looks for package folders `InfoPanel.{Name}/InfoPanel.{Name}.dll`
2. **Host Launch**: a host process is started for each package, connected via named pipe
3. **Loading**: The plugin assembly is loaded in an isolated `AssemblyLoadContext`
4. **Initialization**: `Initialize()` is called once
5. **Container Setup**: `Load()` is called — register all containers and data items here
6. **Config Restore**: If the plugin implements `IPluginConfigurable`, stored config values are applied via `ApplyConfig()` calls
7. **Image Setup**: If `IPluginImageProvider` is implemented, `OnImageBuffersReady()` is called with writers
8. **Updates**: `UpdateAsync()` is called every `UpdateInterval` (or `Update()` every 100 ms when it is zero or less), the first once the steps above are done, so it sees the restored settings
9. **Shutdown**: `Close()` is called when InfoPanel exits or the plugin is turned off or reloaded

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
2. Copy the output folder (DLL, dependencies, and `PluginInfo.ini`) to `plugins/InfoPanel.YourPluginName/` in the engine's data folder ([above](#infopanel-1x-and-20)), and start the engine again
3. The folder must follow the convention: `InfoPanel.YourPluginName/InfoPanel.YourPluginName.dll` (1.x: `YourPluginName/YourPluginName.dll` under `%ProgramData%\InfoPanel\plugins\`)

### Listing in the Plugin Browser

To make your plugin discoverable inside InfoPanel 1.x, add it to the community plugin registry. See the [Plugin Registry Guide](PLUGIN-REGISTRY.md) for instructions. 2.0 has no plugin browser yet.

### Distributing Your Plugin

Create a ZIP file with this structure:

```
YourPluginName.zip
└── YourPluginName/
    ├── YourPluginName.dll
    ├── [dependency DLLs]
    └── PluginInfo.ini
```

In 2.0, users extract the ZIP into the data folder's `plugins/` and start the engine again. In 1.x, users can install by:
- Extracting the ZIP to `%ProgramData%\InfoPanel\plugins\`
- Or using the "Add Plugin from ZIP" option in InfoPanel's Plugins page

## Debugging

Use the Plugin Simulator to run your plugin without InfoPanel. It loads your DLL as the host does, applies settings, runs actions and prints the readings, a number your plugin can't read as "–":

```bash
dotnet run --project InfoPanel.Plugins.Simulator -- <path to your plugin's DLL> [plugin id] [options]
```

| Option | What |
|---|---|
| `--set key=value` | Applies a setting (repeatable) to every plugin with it, after `Load()`, as the host does; the value as the setting's type takes it and InfoPanel would accept it (`true`/`false`, a whole number, a finite number with a `.`, either within the setting's `MinValue` and `MaxValue`; one of a choice's options; text of at most 4,096 characters) |
| `--action <method>` | Shows the action (repeatable), by its method's name, and runs it only with `--yes` |
| `--yes` | Runs the actions `--action` names |
| `--once` | One round (each plugin's `UpdateAsync`, or `Update`, once), printed, then exits: 0, or 1 when a plugin failed to load, list or take a setting, run an action, update, give an entry's value or close (an entry that can't be read prints as failed, the rest as usual). For scripts and quick checks |

Without `--once` the simulator prints the readings every round until you stop it. Arguments it can't take (a setting or action no plugin has, a value its setting can't take) exit 2, saying why, before any setting is applied. Your plugin's `PluginLog` lines go nowhere there. To step through your code, start the simulator from your IDE with the same arguments.

## Best Practices

1. **Use meaningful IDs**: Plugin and container IDs should be stable across versions — they're used for data binding
2. **Handle exceptions**: Wrap potentially problematic code in try-catch blocks — unhandled exceptions in `UpdateAsync` will be reported but won't crash the host
3. **Clean up resources**: Dispose of resources in `Close()` — the host process will terminate, but clean shutdown is preferred
4. **Optimize updates**: Keep `UpdateAsync()` fast — long-running updates block the next cycle
5. **Thread safety**: values may be set from any thread ([Threads](#threads)); image writers may be read concurrently — always call `Invalidate()` after completing a frame
6. **Say what you don't know**: set `float.NaN`, never a made-up 0 ([Numbers a Plugin Can't Read](#numbers-a-plugin-cant-read))
7. **Minimize allocations**: Reuse data objects where possible; avoid allocating new `PluginSensor`/`PluginText` instances every update
8. **Use delta-friendly data**: The host only transmits changed values — keep sensor values stable when unchanged to reduce IPC overhead

---

For questions or help with plugin development, join the [InfoPanel Discord][discord] community.

[discord]: https://discord.gg/aNGeJxjE7Q

## Panel Drivers

InfoPanel 2.0 drives USB panels through **panel drivers**, a kind of plugin of their own: everything above is about sensor plugins. A panel driver targets **.NET 10** too and references `InfoPanel.Panels.Sdk` (in this repository) instead of `InfoPanel.Plugins`; a project references one SDK, and a package holds one or the other. What the engine does with a driver: [docs/2.0/panels.md](docs/2.0/panels.md); how it finds, runs and bounds a panel package: [docs/2.0/plugins.md](docs/2.0/plugins.md#panel-packages).

Adding a family, from the package and its `panel.json` to the tests without a device, the conformance suite and the pull request: [docs/2.0/panel-drivers.md](docs/2.0/panel-drivers.md).

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
