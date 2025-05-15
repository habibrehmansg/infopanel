# InfoPanel

<p align=center>
  <a href="https://www.infopanel.net">
    <img src="Images/logo.png" width=60/>
  </a>
</p>

<p align=center>InfoPanel is a powerful desktop visualization software that transforms how you monitor your system. It displays hardware information on your desktop or external displays as sensor panels, with special support for USB LCD panels like BeadaPanel and Turing Smart Screen/Turzx.</span>

<br />

[Releases][release] | [Reddit][reddit] | [Website][website] | [HWiNFO Forum][forum] | [Discord][discord] | [Microsoft Store][msstore]

![build status](https://github.com/habibrehmansg/infopanel/actions/workflows/dotnet-desktop.yml/badge.svg?branch=main) 

## Features

- **Multiple Data Sources**: 
  - HWiNFO integration via Shared Memory (SHM) for extensive hardware monitoring
  - LibreHardwareMonitor for additional sensor data without requiring HWiNFO
  - Extensible plugin system with built-in and third-party plugins

- **Display Options**:
  - Desktop overlay with customizable transparency and positioning
  - External display support for monitors
  - USB LCD panel support including BeadaPanel via WinUSB API
  - Multiple visualization types: text, gauges, graphs, bars, donuts, and images

- **Advanced Visualization**:
  - GIF animation support for dynamic visualizations
  - High refresh rates for smooth updates
  - Customizable layouts, colors, and fonts
  - Multiple profiles for different use cases or displays

- **Plugin System**:
  - Built-in system information plugins (CPU, memory, network, drives)
  - Weather information integration
  - Support for third-party plugins
  - Sensor data tables with customizable formatting

![InfoPanel](./Images/infopanel-design-view.png)

## Supported Hardware

- All hardware sensors exposed by HWiNFO
- CPU, GPU, RAM, storage, and network monitoring via LibreHardwareMonitor
- BeadaPanel USB LCD panels (all models supported)
- TuringPanel/TURZX displays (Models A, C, and E)
- Any standard monitor or display

For detailed information about supported panels and recommendations, see our [Display Panels Guide](PANELS.md).

## Usage

1. Install either HWiNFO (with Shared Memory support enabled) or use the built-in LibreHardwareMonitor integration
2. Install InfoPanel from the [Microsoft Store][msstore] or the [website][website]
3. Configure your display profile with sensors, gauges, and visualizations
4. Customize layouts with drag-and-drop interface
5. Connect USB displays or position on your desktop
6. (Optional) Install additional plugins for enhanced functionality

## Plugins

InfoPanel features a robust plugin system that extends its capabilities:

### Built-in Plugins
- **System Info Plugin**: CPU usage, memory usage, process statistics, and system uptime
- **Network Info Plugin**: Network interfaces, IP addresses, and connection statistics
- **Drive Info Plugin**: Storage device information and usage statistics
- **Volume Plugin**: Audio volume control and monitoring
- **Weather Plugin**: Current weather conditions and forecasts

### Community Plugins
- [InfoPanel Spotify Plugin](https://github.com/F3NN3X/InfoPanel.Spotify) - Displays currently playing tracks and album art from Spotify
- [InfoPanel FPS Plugin](https://github.com/F3NN3X/InfoPanel.FPS) - Shows FPS and performance metrics for gaming sessions

### Plugin Development
InfoPanel provides a comprehensive API for plugin development that allows access to:
- Sensor data creation and publishing
- Custom visualizations
- Data tables for complex information
- Configuration interfaces

For detailed instructions on developing plugins, see our [Plugin Development Guide](PLUGINS.md).

## Demo
![InfoPanel Demo](./Images/beadapanel-demo-1.gif)

*A demonstration of InfoPanel in action on a BeadaPanel USB LCD*

## Development

InfoPanel is built with C# and WPF for a modern Windows UI experience. The architecture features:

- Modular design with MVVM pattern
- Extensible plugin system
- DirectX acceleration for UI elements
- High-performance graphics rendering for external displays
- Cross-device synchronization

## License

InfoPanel is licensed under GPL 3.0 - see the [license file][license] for details.

---

InfoPanel is not affiliated with HWiNFO. HWiNFO is a registered trademark of its respective owners.

<!--
References
-->

[reddit]: https://www.reddit.com/r/InfoPanel/
[website]: https://www.infopanel.net
[forum]: https://www.hwinfo.com/forum/threads/infopanel-desktop-visualisation-software.8673/
[discord]: https://discord.gg/aNGeJxjE7Q
[msstore]: https://apps.microsoft.com/store/detail/XPFP7C8H5446ZD
[release]: https://github.com/habibrehmansg/infopanel/releases
[license]: https://github.com/habibrehmansg/infopanel/blob/main/LICENSE