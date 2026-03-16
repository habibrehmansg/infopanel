# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore packages
dotnet restore

# Build Debug (x64 only — the solution does not support x86/AnyCPU)
dotnet build InfoPanel.sln -c Debug

# Build Release
dotnet build InfoPanel.sln -c Release

# Publish for deployment (Windows x64)
dotnet publish InfoPanel/InfoPanel.csproj -c Release -r win-x64 --self-contained -p:PublishProfile=FolderProfile -p:Platform=x64

# Run the main application
dotnet run --project InfoPanel/InfoPanel.csproj

# Run plugin simulator for testing plugins
dotnet run --project InfoPanel.Plugins.Simulator/InfoPanel.Plugins.Simulator.csproj
```

> **Note:** InfoPanel.Extras is built automatically via custom MSBuild targets in `InfoPanel.csproj` (`BuildExtras` / `CopyExtrasToBuildOutput`). No separate build step is needed.

## Architecture Overview

InfoPanel is a WPF desktop application built on .NET 8.0 that displays hardware monitoring data on desktop overlays and USB LCD panels. The codebase follows MVVM architecture with a modular plugin system.

### Projects

| Project | Role |
|---|---|
| **InfoPanel** | Main WPF application (entry point, UI, services, drawing) |
| **InfoPanel.Plugins** | Plugin interface definitions (`IPlugin`, `IPluginSensor`, `BasePlugin`, `IPluginConfigurable`, `PluginActionAttribute`) |
| **InfoPanel.Plugins.Graphics** | Image provider interfaces (`IPluginImageProvider`, `IPluginImageWriter`) and MMF-backed double buffering |
| **InfoPanel.Plugins.Ipc** | IPC interfaces and DTOs for out-of-process communication (`IPluginHostService`, `IPluginClientCallback`) |
| **InfoPanel.Plugins.Host** | Out-of-process plugin host executable (StreamJsonRpc over named pipes) |
| **InfoPanel.Plugins.Loader** | Dynamic plugin loading with assembly isolation (`PluginLoadContext`, `PluginWrapper`) |
| **InfoPanel.Plugins.Simulator** | Test harness for plugin development |
| **InfoPanel.Extras** | Built-in plugins (system info, network, drives, weather) |

> **Local DLL references:** LibreHardwareMonitor DLLs are referenced from `../LibreHardwareMonitor/` relative to the repo root. FlyleafLib.dll is referenced from `../libs/` (custom build with headless renderer fix). Ensure that sibling repos/directories are present when building.

### Application Startup

Entry point is `Program.cs` (`[STAThread] Main`) → `App.xaml.cs` → `OnStartup()`:

1. Single-instance check (prompts to kill existing process)
2. `_host.Start()` — starts the Generic Host, which runs `ApplicationHostService` (an `IHostedService` that creates `MainWindow` and sets up navigation)
3. Flyleaf media engine initialization
4. `ConfigModel.Instance.Initialize()` — loads settings and profiles from disk
5. Default profile creation if first run
6. `HWHash.Launch()` — starts HWiNFO shared-memory reader on its own thread
7. `LibreMonitor.Instance.StartAsync()` — if enabled in settings
8. `PluginMonitor.Instance.StartAsync()`
9. `StartPanels()` — starts BeadaPanel, TuringPanel, and WebServer tasks based on settings

> **Note:** `Startup.cs` exists in the project but is entirely commented out — it is not part of the startup flow.

### Key Singletons

These singletons use the `Lazy<T>` / `Instance` pattern and are **not** in the DI container:

| Singleton | File | Role |
|---|---|---|
| `ConfigModel.Instance` | `Models/ConfigModel.cs` | Settings, profiles, persistence (XML). Thread-safe with locks. |
| `SharedModel.Instance` | `Models/SharedModel.cs` | Shared app state: selected profile, display items, sensor data. Inherits `ObservableObject`. |
| `DisplayWindowManager.Instance` | `DisplayWindowManager.cs` | Manages overlay windows on a dedicated STA thread. |
| `LibreMonitor.Instance` | `Monitors/LibreMonitor.cs` | LibreHardwareMonitor sensor polling. |
| `PluginMonitor.Instance` | `Monitors/PluginMonitor.cs` | Plugin lifecycle management. |
| `BeadaPanelTask.Instance` | `Services/BeadaPanelTask.cs` | BeadaPanel USB device communication. |
| `TuringPanelTask.Instance` | `Services/TuringPanelTask.cs` | TuringPanel USB/serial communication. |
| `WebServerTask.Instance` | `Services/WebServerTask.cs` | Built-in HTTP API and web interface. |
| `ProfilePreviewCoordinator.Instance` | `Services/ProfilePreviewCoordinator.cs` | Batched profile preview rendering. |
| `UndoManager.Instance` | `Services/UndoManager.cs` | Per-profile undo/redo via XML snapshots. |

### DI Container Services

Registered in `App.xaml.cs` via `Host.CreateDefaultBuilder().ConfigureServices(...)`:

- **Hosted:** `ApplicationHostService` (bridges host lifecycle → WPF)
- **Singletons:** `IThemeService`, `ITaskBarService`, `ISnackbarService`, `IContentDialogService`, `IPageService`, `INavigationService`
- **Scoped:** `MainWindow` (as `INavigationWindow`), all Pages and their ViewModels

### Threading Model

| Thread | Purpose |
|---|---|
| **Main UI thread** | WPF dispatcher, MainWindow, all settings pages |
| **DisplayWindowThread** | Dedicated STA thread created by `DisplayWindowManager` with its own `Dispatcher`. Hosts all `DisplayWindow` overlays. |
| **BackgroundTask workers** | Each service (`BeadaPanelTask`, `TuringPanelTask`, `WebServerTask`, `LibreMonitor`, `PluginMonitor`) runs via `Task.Run` from `BackgroundTask` base class with `CancellationToken` and `SemaphoreSlim(1,1)` for thread safety. |
| **HWHash thread** | `HWHash.Launch()` starts a dedicated thread that reads HWiNFO shared memory at a configurable interval. |

> `BackgroundTask` (`Services/BackgroundTask.cs`) is the abstract base class for all long-running services. It provides `StartAsync`, `StopAsync`, `IsRunning`, and a protected `DoWorkAsync(CancellationToken)` method.

### Display System

The drawing system (`InfoPanel/Drawing/`) has multiple implementations:
- `SkiaGraphics` — Primary renderer using SkiaSharp
- `AcceleratedGraphics` — Hardware-accelerated DirectX rendering
- `PanelDraw` — Orchestrates rendering of display items

### DisplayItem Hierarchy

Base class: `DisplayItem` (abstract, in `Models/DisplayItem.cs`) — inherits `ObservableObject`, implements `ICloneable`.

| Category | Types |
|---|---|
| **Text-based** | `TextDisplayItem`, `SensorDisplayItem`, `ClockDisplayItem`, `CalendarDisplayItem` |
| **Table** | `TableSensorDisplayItem` |
| **Images** | `ImageDisplayItem`, `HttpImageDisplayItem`, `SensorImageDisplayItem` |
| **Charts** | `GraphDisplayItem`, `BarDisplayItem`, `DonutDisplayItem` (all inherit abstract `ChartDisplayItem`) |
| **Other** | `GaugeDisplayItem`, `ShapeDisplayItem`, `GroupDisplayItem` (container) |

`SensorDisplayItem` inherits from `TextDisplayItem`. Chart types inherit from abstract `ChartDisplayItem` which inherits from `DisplayItem`.

### Data Sources

| Source | Class | Notes |
|---|---|---|
| **HWiNFO** | `HWHash` (`HWHash/HWHash.cs`) | Reads shared memory; must be running externally |
| **LibreHardwareMonitor** | `LibreMonitor` (`Monitors/LibreMonitor.cs`) | Built-in; requires PawniO driver for low-level access |
| **Plugins** | `PluginMonitor` (`Monitors/PluginMonitor.cs`) | Bundled from `plugins/`; external from `%ProgramData%\InfoPanel\plugins\`. Each plugin runs in a separate host process via StreamJsonRpc over named pipes. |

### USB Panel Support

USB panel communication is in `InfoPanel/TuringPanel/` and `InfoPanel/BeadaPanel/`:
- Uses WinUSB API for BeadaPanel devices
- Serial/USB communication for TuringPanel devices
- Model-specific configurations in database classes

### Plugin Development

Plugins are .NET libraries that:
1. Reference `InfoPanel.Plugins` package
2. Implement `IPlugin` interface (inherit from `BasePlugin`)
3. Use attributes like `[PluginSensor]` to expose data
4. Include a `PluginInfo.ini` manifest file

See `PLUGINS.md` for detailed plugin development guide.

## File Storage

All user data is stored under `%LOCALAPPDATA%/InfoPanel/`:

| Path | Contents |
|---|---|
| `settings.xml` | Application settings |
| `profiles.xml` | Profile list |
| `profiles/{guid}.xml` | Individual profile data (display items) |
| `assets/{guid}/` | Profile-specific images and assets |
| `logs/infopanel-*.log` | Rolling daily logs (7-day retention, 100MB limit) |
| `updates/` | Downloaded update installers (auto-cleaned) |
| `plugins.bin` | Plugin activation state |

External plugin data is stored under `%ProgramData%/InfoPanel/`:

| Path | Contents |
|---|---|
| `plugins/` | User-installed (external) plugins |

## CI/CD

GitHub Actions workflow: `.github/workflows/dotnet-desktop.yml`

- **Triggers:** push to `main`/`master`, tags, pull requests, manual dispatch
- **Runner:** `windows-2022`
- **Steps:** checkout → setup .NET 8.0 → restore → publish (win-x64) → compile Inno Setup installer → upload artifact
- **Releases:** tagged pushes automatically upload the installer to GitHub Releases

## Key Technologies

- **WPF** with **WPF-UI** for modern Windows 11 styling
- **CommunityToolkit.MVVM** for MVVM implementation
- **SkiaSharp** for cross-platform graphics rendering
- **FlyleafLib** for media playback
- **Serilog** for structured logging (see `LoggingGuidelines.md`)
- **LibreHardwareMonitor** for hardware sensor access
- **ASP.NET Core** for built-in web server
- **Sentry** for error tracking

## Development Notes

- The solution targets .NET 8.0 with Windows Desktop runtime (`net8.0-windows10.0.19041.0`)
- **x64 only** — `<Platforms>x64</Platforms>` is set in all csproj files
- Warning level 6 and nullable reference types are enabled
- `AllowUnsafeBlocks` is enabled for performance-critical code
- No unit test projects currently exist
- Plugins are loaded from the `plugins` directory at runtime

## API Client

The C# API client (`InfoPanel/ApiClient/InfoPanelApiClient.cs`) is auto-generated from the OpenAPI spec using NSwag. It is committed to source control — no build-time generation.

- **Spec:** `InfoPanel/ApiClient/openapi.json` (OpenAPI 3.0, fetched from the live API)
- **Config:** `InfoPanel/ApiClient/nswag.json`
- **Generated client:** `InfoPanel/ApiClient/InfoPanelApiClient.cs`
- **Wrapper service:** `InfoPanel/Services/InfoPanelApiService.cs` (singleton with `Instance` pattern)

### Regenerating the client

When the API changes, run:

```powershell
pwsh scripts/generate-api-client.ps1
```

This fetches the latest spec from `https://api.infopanel.net/openapi.json` and regenerates the client. Review the diff in both `openapi.json` and `InfoPanelApiClient.cs` before committing.

> **Note:** NSwag is installed as a local dotnet tool (`.config/dotnet-tools.json`). Run `dotnet tool restore` after cloning.

## Related Documentation

- `PLUGINS.md` — Plugin development guide (for plugin authors)
- `PLUGIN-ARCHITECTURE.md` — Plugin system internals (for contributors)
- `PANELS.md` — Hardware panel support and models
- `LoggingGuidelines.md` — Serilog usage standards and log levels
