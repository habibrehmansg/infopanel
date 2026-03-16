# InfoPanel Plugin Architecture

This document describes the internal architecture of InfoPanel's plugin system. It is intended for InfoPanel contributors and advanced developers who want to understand how plugins are loaded, hosted, and communicate with the main application.

For writing plugins, see [PLUGINS.md](PLUGINS.md).

## System Overview

```
┌─────────────────────┐         Named Pipe          ┌─────────────────────┐
│    InfoPanel App     │◄──── StreamJsonRpc ────────►│  Plugin Host Process │
│                      │                             │                      │
│  PluginMonitor       │    IPluginHostService ──►   │  HostService         │
│  PluginProcessMgr    │    ◄── IPluginClientCb      │  PluginWrapper(s)    │
│  PluginHostConnection│                             │  SensorSnapshotMgr   │
│  RemotePluginWrapper │         MMF (images)        │  PluginImageWriter   │
└─────────────────────┘◄────── shared memory ──────►└─────────────────────┘
```

Each plugin runs in a separate host process. The main application communicates with each host via JSON-RPC over a named pipe. Image data is shared via memory-mapped files (MMF) for zero-copy transfer.

## Projects and Responsibilities

| Project | Role |
|---|---|
| **InfoPanel.Plugins** | Public API for plugin authors: `IPlugin`, `BasePlugin`, data types (`PluginSensor`, `PluginText`, `PluginTable`), `IPluginContainer`, `PluginActionAttribute`, `IPluginConfigurable`, `PluginConfigProperty` |
| **InfoPanel.Plugins.Graphics** | Image provider API: `IPluginImageProvider`, `IPluginImageWriter`, `PluginImageDescriptor`. Contains `PluginImageWriter` — the MMF-backed double-buffer implementation |
| **InfoPanel.Plugins.Ipc** | IPC contract: `IPluginHostService` (host-side methods), `IPluginClientCallback` (app-side notifications), and all DTO types for serialization |
| **InfoPanel.Plugins.Host** | Standalone executable that hosts one plugin assembly. Connects to the app's named pipe, loads the plugin via `PluginLoader`, and runs the update loop |
| **InfoPanel.Plugins.Loader** | `PluginLoader` (discovers and instantiates plugins), `PluginLoadContext` (assembly isolation), `PluginWrapper` (lifecycle management), `PluginDescriptor` (metadata), `PluginInfo` (INI parser) |

## Assembly Isolation

Each plugin is loaded in its own `PluginLoadContext` (inherits `AssemblyLoadContext`), which:

- Uses `AssemblyDependencyResolver` to resolve the plugin's dependencies from its directory
- Falls back to the host context for **shared assemblies**: `InfoPanel.Plugins.Graphics` and `SkiaSharp` — this is required so that types like `IPluginImageWriter` and `SKBitmap` are shared between the host and plugin without type-mismatch errors
- All other assemblies are isolated, preventing version conflicts between plugins

## IPC Protocol

Communication uses [StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc) over `NamedPipeClientStream`/`NamedPipeServerStream`.

### IPluginHostService (app → host)

Called by the main application to control the plugin host:

| Method | Description |
|---|---|
| `InitializeAsync()` | Load plugin assembly, call `Initialize()` and `Load()`, start update loop. Returns `List<PluginMetadataDto>` |
| `InvokeActionAsync(pluginId, methodName)` | Invoke a `[PluginAction]` method on the plugin |
| `GetConfigPropertiesAsync(pluginId)` | Get current configuration properties |
| `ApplyConfigAsync(pluginId, key, value)` | Apply a config change and return updated properties |
| `ShutdownAsync()` | Stop updates, call `Close()`, clean up resources |

### IPluginClientCallback (host → app)

Notifications sent from the host back to the main application:

| Method | Description |
|---|---|
| `OnPluginsLoaded(plugins, containers)` | Plugin metadata and initial container structure |
| `OnSensorUpdate(updates)` | Batched delta updates (only changed values) |
| `OnPluginError(pluginId, message)` | Error notification |
| `OnPerformanceUpdate(performances)` | CPU and update-time metrics |
| `OnImageResize(pluginId, descriptor)` | Image buffer was resized — app must re-map the MMF |

### DTO Hierarchy

```
PluginMetadataDto
  ├── PluginActionDto[]
  ├── PluginConfigPropertyDto[]
  └── ImageDescriptorDto[]

ContainerDto
  └── EntryDto[]
        ├── SensorValueDto?
        ├── TextValue?
        └── TableValueDto?
              └── TableCellDto[][]

SensorUpdateBatchDto
  └── EntryUpdateDto[]

PluginPerformanceDto
```

## Plugin Host Process

### Entry Point

`InfoPanel.Plugins.Host/Program.cs` — a console application that:

1. Parses CLI arguments: `--pipe <name>` and `--plugin <path>` (both required)
2. Configures Serilog logging to `%LOCALAPPDATA%/InfoPanel/logs/plugin-host-{name}.log`
3. Connects to the named pipe (30-second timeout)
4. Attaches `HostService` as the JSON-RPC target
5. Waits for the RPC connection to complete

> The main InfoPanel process also supports `--plugin-host` mode (in `InfoPanel/Program.cs`), allowing it to run as an embedded host when needed.

### HostService

`HostService` implements `IPluginHostService` and manages:

- **Plugin loading**: Uses `PluginLoader.InitializePlugin()` to load the assembly and create `PluginWrapper` instances
- **Update loop**: 100ms poll interval, calls `Plugin.Update()` or `Plugin.UpdateAsync()` depending on `UpdateInterval`
- **Performance loop**: 2000ms interval, computes CPU usage via `Process.TotalProcessorTime` delta
- **Image setup**: Creates `MemoryMappedFile` instances named `InfoPanel.Image.{ProcessId}.{PluginId}.{ImageId}` for each image descriptor
- **Config application**: Handles `JToken` deserialization and type coercion for config values

## Image System

### Memory-Mapped File Layout

Each image uses a single MMF with double buffering:

```
Offset  Size              Content
──────  ────              ───────
0       4 bytes (int)     Active buffer index (0 or 1)
4       4 bytes (int)     Width
8       4 bytes (int)     Height
12      4 bytes           Reserved
16      W × H × 4 bytes  Buffer 0 (RGBA pixels)
16+B    W × H × 4 bytes  Buffer 1 (RGBA pixels)

Total size: 16 + 2 × (Width × Height × 4)
```

### Double Buffering Protocol

1. The plugin draws to the **inactive** buffer via `IPluginImageWriter.Bitmap` (which wraps an `SKBitmap` pointing at the inactive buffer's memory)
2. When drawing is complete, `Invalidate()` atomically swaps the active buffer index using `Interlocked.Exchange()`
3. The main application reads from the **active** buffer — no locking required, no tearing possible
4. After swap, the `SKBitmap` is re-pointed to the new inactive buffer for the next frame

### Resize Protocol

When `IPluginImageWriter.Resize(width, height)` is called:

1. A new MMF is created with a versioned name (e.g., `InfoPanel.Image.{PID}.{PluginId}.{ImageId}.v1`)
2. The old MMF is disposed
3. The `Resized` event fires in the host
4. `OnImageResize` is sent to the main application so it can re-map to the new MMF

## Performance Tracking

The host process tracks two metrics per plugin:

- **Update time**: Wall-clock duration of each `UpdateAsync()` call, reported as `UpdateTimeMilliseconds`
- **CPU usage**: Computed as `(cpuDelta_ms / (elapsed_ms × ProcessorCount)) × 100`, reported as `CpuPercent`

These are sent via `OnPerformanceUpdate` every 2 seconds.

## Delta Updates

To minimize IPC overhead, only changed sensor values are transmitted.

`SensorSnapshotManager` (in `InfoPanel.Plugins.Host`) maintains a hash of each entry's last-sent state:

- **Sensors**: `HashCode.Combine(Value, ValueMin, ValueMax, ValueAvg)`
- **Text**: String hash code
- **Tables**: Row count + column count + cell content hashes

On each update cycle, `GetChangedEntries()` compares current hashes against cached hashes and returns only the entries that differ. Key strings (`{pluginId}/{containerId}/{entryId}`) are cached to avoid repeated allocation.

## Plugin Loading Flow

```
PluginMonitor.StartAsync()
  │
  ├── Discover plugin folders
  │     ├── Bundled: plugins/InfoPanel.Extras/ (whitelisted)
  │     └── External: %ProgramData%\InfoPanel\plugins\*\
  │           └── Auto-extract any InfoPanel.*.zip files
  │
  ├── For each plugin folder:
  │     ├── PluginLoader.GetPluginInfo() → read PluginInfo.ini
  │     └── Create PluginDescriptor
  │
  └── PluginProcessManager.StartPlugin()
        ├── Spawn host process: InfoPanel.Plugins.Host.exe --pipe <name> --plugin <path>
        ├── Create NamedPipeServerStream, wait for connection
        ├── Attach StreamJsonRpc with IPluginClientCallback
        ├── Call InitializeAsync() → host loads plugin, returns metadata
        ├── Create RemotePluginWrapper proxies for each plugin/container/entry
        └── Register sensors in PluginMonitor.SENSORHASH
```

### Retry Logic

If a host process crashes, `PluginProcessManager` will retry up to 3 times within a 60-second window before marking the plugin as failed.
