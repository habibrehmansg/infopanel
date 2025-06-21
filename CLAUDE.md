# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore packages
dotnet restore

# Build Debug
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

## Architecture Overview

InfoPanel is a WPF desktop application built on .NET 8.0 that displays hardware monitoring data on desktop overlays and USB LCD panels. The codebase follows MVVM architecture with a modular plugin system.

### Core Projects

- **InfoPanel** - Main WPF application
  - Entry: `App.xaml.cs` ’ `Startup.cs` ’ `MainWindow.xaml`
  - MVVM structure: ViewModels handle logic, Views handle UI
  - Background services run display updates and hardware communication
  - Drawing abstraction supports multiple graphics backends (SkiaSharp, DirectX)

- **InfoPanel.Plugins** - Plugin interface definitions
  - `IPlugin` - Base plugin interface
  - `IPluginSensor` - For sensor data providers
  - `IPluginText/Table` - For display elements
  - All plugins must inherit from `BasePlugin`

- **InfoPanel.Plugins.Loader** - Dynamic plugin loading
  - Discovers plugins in the `plugins` directory
  - Loads assemblies in isolated contexts
  - Manages plugin lifecycle and dependencies

- **InfoPanel.Extras** - Built-in plugins
  - Ships with the application in the plugins folder
  - Provides system info, network, drives, weather functionality

### Key Services and Background Tasks

Located in `InfoPanel/Services/`:
- `PanelDrawTask` - Renders visualizations at high frame rates
- `BeadaPanelTask/TuringPanelTask` - USB panel communication
- `WebServerTask` - HTTP API and web interface
- Hardware monitors - Collect sensor data from HWiNFO/LibreHardwareMonitor

### Display System

The drawing system (`InfoPanel/Drawing/`) has multiple implementations:
- `SkiaGraphics` - Primary renderer using SkiaSharp
- `AcceleratedGraphics` - Hardware-accelerated DirectX rendering
- `PanelDraw` - Orchestrates rendering of display items

Display items (`InfoPanel/Models/`) represent visualizations:
- `SensorDisplayItem` - Text-based sensor values
- `GaugeDisplayItem` - Circular gauge visualizations  
- `ChartDisplayItem` - Graphs, bars, and donut charts
- `ImageDisplayItem` - Static and animated images

### USB Panel Support

USB panel communication is in `InfoPanel/TuringPanel/` and `InfoPanel/BeadaPanel/`:
- Uses WinUSB API for BeadaPanel devices
- Serial/USB communication for TuringPanel devices
- Model-specific configurations in database classes

### Plugin Development

Plugins are .NET libraries that:
1. Reference `InfoPanel.Plugins` package
2. Implement `IPlugin` interface
3. Use attributes like `[PluginSensor]` to expose data
4. Include a `PluginInfo.ini` manifest file

See `PLUGINS.md` for detailed plugin development guide.

## Key Technologies

- **WPF** with **WPF-UI 3.1.1** for modern Windows 11 styling
- **CommunityToolkit.MVVM** for MVVM implementation
- **SkiaSharp** for cross-platform graphics rendering
- **Serilog** for structured logging (see `LoggingGuidelines.md`)
- **LibreHardwareMonitor** for hardware sensor access
- **ASP.NET Core** for built-in web server
- **Sentry** for error tracking

## Development Notes

- The solution uses .NET 8.0 with Windows Desktop runtime
- Warning level 6 and nullable reference types are enabled
- No unit test projects currently exist
- Plugins are loaded from the `plugins` directory at runtime
- Configuration is stored in `%APPDATA%/InfoPanel/`