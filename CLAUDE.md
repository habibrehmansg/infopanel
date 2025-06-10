# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

InfoPanel is a Windows desktop visualization software that displays hardware information on desktop overlays or external displays (USB LCD panels, Turing Smart Screens). It's built with C# WPF using .NET 8.0 and features a plugin system for extensibility.

## Build Commands
- Claude environment is in WSL: It cannot build for Windows. 
  - Ask the developer for assistance in build and verification instead.

## Architecture Overview

### Core Components
- **InfoPanel** - Main WPF application with MVVM architecture
- **InfoPanel.Plugins** - Plugin interface definitions and base classes
- **InfoPanel.Plugins.Loader** - Dynamic plugin loading system using AssemblyLoadContext
- **InfoPanel.Extras** - Built-in plugins (System Info, Network, Drive Info, Weather, Volume)

### Project Structure Deep Dive

**InfoPanel/ (Main Application)**
- `Services/` - Background tasks and application services
  - `BeadaPanelTask.cs`, `TuringPanelTask.cs` - Hardware panel communication
  - `PanelDrawTask.cs` - Rendering pipeline management
  - `WebServerTask.cs` - HTTP API for external integrations
- `ViewModels/` - MVVM ViewModels with CommunityToolkit.Mvvm
  - `DesignViewModel.cs` - UI layout and design configuration
  - `SettingsViewModel.cs` - Application settings management
  - `Components/` - Reusable ViewModel components
- `Views/` - WPF XAML UI components
  - `Pages/` - Main application pages
  - `Components/` - Reusable UI components and properties panels
  - `Converters/` - Data binding value converters
- `Models/` - Data models and business logic
  - `Profile.cs` - User configuration profiles
  - `DisplayItem.cs` - UI element definitions
  - `BeadaPanelDevice.cs` - Hardware device models
- `Drawing/` - Graphics rendering pipeline
  - `PanelDraw.cs` - Main drawing coordinator
  - `SkiaGraphics.cs`, `AcceleratedGraphics.cs` - Different rendering backends
- `BeadaPanel/` - BeadaPanel USB LCD communication
- `TuringPanel/` - Turing Smart Screen communication
- `Monitors/` - Hardware sensor data collection
- `Utils/` - Utility classes and helpers

**InfoPanel.Plugins/ (Plugin System)**
- Interface definitions (`IPlugin`, `IPluginSensor`, etc.)
- Base classes (`BasePlugin`, `PluginSensor`, etc.)
- Plugin container system for data organization

**InfoPanel.Plugins.Loader/ (Plugin Loading)**
- Dynamic assembly loading with isolation
- Plugin lifecycle management
- Plugin discovery and initialization

**InfoPanel.Extras/ (Built-in Plugins)**
- System information (CPU, Memory, Storage)
- Network monitoring
- Weather integration
- Volume control
- Drive space monitoring

### Key Architectural Patterns
- **Plugin System**: Plugins implement `IPlugin` interface, loaded dynamically from assemblies
- **Data Sources**: Hardware monitoring via HWiNFO (Shared Memory) and LibreHardwareMonitor
- **Rendering Pipeline**: Multiple graphics backends (WPF, SkiaSharp, DirectX) for different display targets
- **Background Tasks**: Sensor monitoring, panel updates, and web server run as background services

### Display Targets
- Desktop overlay windows (WPF/SkiaSharp)
- BeadaPanel USB LCD displays (WinUSB API)
- Turing Smart Screen panels (Models A, C, E)
- Standard external monitors

### Plugin Architecture
Plugins provide data through containers holding various data types:
- `PluginSensor` - Numeric values with units
- `PluginText` - String values  
- `PluginTable` - Tabular data for complex displays

Plugin lifecycle: Initialize → Load containers → Periodic UpdateAsync → Close

### Project Dependencies
- **Graphics**: SkiaSharp, System.Drawing.Common (Deprecated in favor of Skiasharp)
- **Hardware**: LibreHardwareMonitor (for sensors), HidSharp, LibUsbDotNet
- **Video**: FFmpeg.AutoGen (native FFmpeg support for video backgrounds)
- **UI**: WPF-UI, MahApps.Metro
- **Infrastructure**: Microsoft.Extensions.Hosting, AutoMapper

## Testing & Quality Assurance

**Testing Approach:**
InfoPanel does not use formal unit testing frameworks (MSTest, xUnit, NUnit). Testing is primarily manual and plugin-focused.

## Development Workflow

**MVVM Architecture Guidelines:**
- **ViewModels** (`InfoPanel/ViewModels/`): Implement MVVM pattern using CommunityToolkit.Mvvm
- **Views** (`InfoPanel/Views/`): XAML files for UI components, organized by function
- **Models** (`InfoPanel/Models/`): Data models and business logic
- **Services** (`InfoPanel/Services/`): Background tasks and application services

**UI Development Patterns:**
- Use WPF-UI and MahApps.Metro for consistent styling
- Converters defined in `App.xaml` for data binding
- Custom controls in `InfoPanel/Views/Controls/`
- Template selectors for dynamic UI rendering

**Plugin Development Workflow:**
1. Create new class library targeting .NET 8.0
2. Reference `InfoPanel.Plugins` project
3. Implement `IPlugin` interface
4. Use base classes: `BasePlugin`, `PluginSensor`, `PluginText`, `PluginTable`
5. Test with Plugin Simulator before integration

**Code Style & Conventions:**
- Follow standard C# naming conventions
- Use nullable reference types (`<Nullable>enable</Nullable>`)
- Async methods should end with `Async` suffix
- Use dependency injection for services via Microsoft.Extensions.Hosting

## Development Notes

- Solution targets x64 architecture only
- Requires .NET 8.0 Windows runtime
- Plugin development uses separate class libraries targeting .NET 8.0
- FFmpeg video support is experimental (current branch: ffmpeg-native)
- Plugin testing can be done via InfoPanel.Plugins.Simulator project
- Current development focus: BeadaPanel multi-device support

## Memories
- Converters are defined in App.xaml
- No formal testing framework used - relies on Plugin Simulator and manual testing
- Plugin Simulator is primary tool for plugin development and debugging
- Current branch focuses on BeadaPanel multi-device support
- Graphics rendering supports multiple backends (WPF, SkiaSharp, DirectX)
- Hardware monitoring via HWiNFO shared memory and LibreHardwareMonitor
- FFmpeg video support exists but is experimental
- Solution uses dependency injection via Microsoft.Extensions.Hosting