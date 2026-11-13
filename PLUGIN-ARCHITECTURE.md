# InfoPanel Plugin Architecture

This document describes the internal architecture of InfoPanel's plugin system. It is intended for InfoPanel contributors and advanced developers who want to understand how plugins are loaded, hosted, and communicate with the main application. It describes InfoPanel 2.0's engine as the main application; what 2.0 does with plugins, and where it keeps them, is in [docs/2.0/plugins.md](docs/2.0/plugins.md).

For writing plugins, see [PLUGINS.md](PLUGINS.md).

## System Overview

```
┌────────────────────────┐          Named Pipe         ┌────────────────────────────┐
│  InfoPanel engine      │◄────── StreamJsonRpc ──────►│  Plugin host process       │
│                        │                             │  (InfoPanel --plugin-host) │
│  PluginHostManager     │    IPluginHostService ──►   │  HostService               │
│  PluginHostRunner      │   ◄── IPluginClientCallback │  PluginWrapper(s)          │
│  PluginHostConnection  │                             │  SensorSnapshotManager     │
│  PluginSensorSource    │    MMF (images; not read    │  PluginImageWriter         │
└────────────────────────┘         by 2.0 yet)         └────────────────────────────┘
```

Each plugin package (a folder `InfoPanel.{Name}` with its DLL) runs in a host process of its own; the plugins in one DLL share it. The engine communicates with each host via JSON-RPC over a named pipe it serves for the current user only. Image data is shared via memory-mapped files (MMF) for zero-copy transfer; 2.0's engine doesn't read plugin images yet.

## Projects and Responsibilities

| Project | Role |
|---|---|
| **InfoPanel.Plugins** | Public API for plugin authors: `IPlugin`, `BasePlugin`, data types (`PluginSensor`, `PluginText`, `PluginTable`), `IPluginContainer`, `PluginActionAttribute`, `IPluginConfigurable`, `PluginConfigProperty` |
| **InfoPanel.Plugins.Graphics** | Image provider API: `IPluginImageProvider`, `IPluginImageWriter`, `PluginImageDescriptor`. Contains `PluginImageWriter` — the MMF-backed double-buffer implementation |
| **InfoPanel.Plugins.Ipc** | IPC contract: `IPluginHostService` (host-side methods), `IPluginClientCallback` (app-side notifications), and all DTO types for serialization |
| **InfoPanel.Plugins.Host** | A library that hosts one plugin assembly, run by the engine executable as its plugin host mode (`InfoPanel --plugin-host`, `PluginHostEntry`). Connects to the engine's named pipe, loads the plugin via `PluginLoader`, and runs the update loop |
| **InfoPanel.Plugins.Loader** | `PluginLoader` (discovers and instantiates plugins), `PluginLoadContext` (assembly isolation), `PluginWrapper` (lifecycle management), `PluginDescriptor` (metadata), `PluginInfo` (INI parser) |
| **InfoPanel** (`Plugins/`) | The engine's side: `PluginPackages` (finds the packages), `PluginHostManager` (one `PluginHostRunner` per package, started and stopped together), `PluginHostRunner` (starts a host, restarts it, stops it), `PluginHostConnection` (the pipe and the JSON-RPC connection), `PluginSensorSource` (the `plugins` sensor source, under 1.x's keys) |

## Assembly Isolation

Each plugin is loaded in its own `PluginLoadContext` (inherits `AssemblyLoadContext`), which:

- Uses `AssemblyDependencyResolver` to resolve the plugin's dependencies from its directory
- Falls back to the host context for **shared assemblies**: `InfoPanel.Plugins.Graphics` and `SkiaSharp` — this is required so that types like `IPluginImageWriter` and `SKBitmap` are shared between the host and plugin without type-mismatch errors
- All other assemblies are isolated, preventing version conflicts between plugins

## IPC Protocol

Communication uses [StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc) over `NamedPipeClientStream`/`NamedPipeServerStream`.

### IPluginHostService (app → host)

Called by the main application to control the plugin host (2.0's engine calls `InitializeAsync` and `ShutdownAsync`; the others are 1.x's):

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
| `OnSensorUpdate(updates)` | Batched delta updates (only changed values); a plugin's picture (`PluginImage`) goes alone in one of its own |
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
        └── ImageValue?   (a picture's bytes as base64, Type "image"; none: no picture)

PluginPerformanceDto
```

## Plugin Host Process

### Entry Point

`InfoPanel.Plugins.Host/PluginHostEntry.cs`, which the engine's `InfoPanel/Program.cs` runs when its first argument is `--plugin-host`:

1. Parses the arguments: `--pipe <name>`, `--plugin <dll>`, `--log-dir <dir>` and `--settings-dir <dir>` (required), `--log-level <level>` (`information` by default)
2. Configures Serilog logging to `{log-dir}/{plugin folder}-{yyyyMMdd}.log` (daily, three kept, 10 MiB a file)
3. Connects to the named pipe (30-second timeout)
4. Attaches `HostService` as the JSON-RPC target; plugins' settings go to `{settings-dir}/{plugin id}.json`
5. Waits for the RPC connection to complete; when the pipe ends without a shutdown, the plugins get 2 s to close and the process exits

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
- **CPU usage**: Computed per CPU as `(cpuDelta_ms / elapsed_ms) × 100` (`HostService.CpuPercent`: 100 is one busy CPU, never below 0), reported as `CpuPercent`. The engine shows it as it counts its own CPU (`PluginHostRunner.CpuShown`): per CPU on macOS and Linux, up to 100 × the logical CPUs; on Windows a share of the whole machine's, the report over the CPUs, 0–100

These are sent via `OnPerformanceUpdate` every 2 seconds.

## Delta Updates

To minimize IPC overhead, only changed sensor values are transmitted.

`SensorSnapshotManager` (in `InfoPanel.Plugins.Host`) maintains a hash of each entry's last-sent state:

- **Sensors**: `HashCode.Combine(Value, ValueMin, ValueMax, ValueAvg)`
- **Text**: String hash code
- **Tables**: Row count + column count + cell content hashes

On each update cycle, `GetChangedEntries()` compares current hashes against cached hashes and returns only the entries that differ. Key strings (`{pluginId}/{containerId}/{entryId}`) are cached to avoid repeated allocation.

An entry counts as sent only once its message was written (`MarkSent`), so a change held back goes in a later cycle with its newest value. Pictures (`GetChangedImages()`): an entry's picture is sent when its SHA-256 changed (hashed at most once a second), alone in a message, at most one new picture an entry a second, the entries that waited longest first; each is reserved just before its send (`TryReserve`) and holds its reservation through it (`Release`).

### The wire budget

The engine closes a host that has more than 1,024 messages, or 32 MiB of them, waiting for it to handle (`PendingBudget`), so `WireBudget` holds everything the host sends to what the engine holds over a 5 s stall: 16 MiB at once and 3 MiB a second, 64 messages at once and 32 a second (31 MiB and 224 messages in any 5 s), the numbers in `IpcLimits`. Messages of readings take from it and from their own budget (8 MiB at once, 2 MiB a second), pictures from it and from theirs (12 MiB at once, 2 MiB a second; 16 at once, 16 a second), each counted by `JsonSize`'s estimate (an upper bound); readings that find no room end the cycle's readings (`HostService.RunUpdateCycleAsync`), in the order their changes began to wait (`WaitingOrder`), and a picture that finds none ends the cycle's pictures. The catalogue counts once, at the start; an error report, a resize or the processor use that finds no room is dropped. The engine checks a host's pictures again as it takes them (`PluginSensorSource`: only a listed image entry's, within the same rates and 5 s more burst, before anything is decoded), and takes an update only in the source's generation it read with the connection still current (`PluginHostRunner`'s callback, `PluginSensorSource.EpochOf`), so a call of a host that has been replaced takes nothing.

## Plugin Loading Flow

```
PluginHostManager.StartAllAsync()        (once the engine listens)
  │
  ├── PluginPackages.Find()
  │     ├── Bundled: {engine folder}/plugins/InfoPanel.*/
  │     └── Installed: {data folder}/plugins/InfoPanel.*/
  │
  └── For each package, PluginHostRunner:
        ├── Create NamedPipeServerStream (ipph-{16 hex}, current user only)
        ├── Spawn host process: InfoPanel --plugin-host --pipe <name> --plugin <dll> --log-dir … --settings-dir … --log-level …
        ├── Attach StreamJsonRpc with IPluginClientCallback
        ├── Call InitializeAsync() → host loads plugin, returns metadata
        └── OnPluginsLoaded → PluginSensorSource lists the entries as sensors
```

### Retry Logic

If a host process exits or disconnects unexpectedly, or fails to start, `PluginHostRunner` starts it again after 2 s, up to 3 times within a 60-second window (counted from the first failure), before marking the package as failed ([docs/2.0/plugins.md](docs/2.0/plugins.md#hosts)).
