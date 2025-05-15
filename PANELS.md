# InfoPanel Display / Panel Guide

## Table of Contents
- [Introduction](#introduction)
- [Supported Data Sources](#supported-data-sources)
- [Display Types Comparison](#display-types-comparison)
- [BeadaPanel Models](#beadapanel-models)
- [Turing Smart Screen/TURZX Models](#turing-smart-screenturzx-models)
- [Connection and Setup](#connection-and-setup)
- [Troubleshooting](#troubleshooting)
- [Recommendations](#recommendations)

## Introduction

InfoPanel supports various types of display panels to visualize your system information, ranging from desktop windows to dedicated USB LCD panels. This guide provides detailed information about the supported panels, their capabilities, and recommendations based on performance characteristics.

## Supported Data Sources

InfoPanel can use different sources for system data:

1. **HWiNFO Integration** (Recommended): 
   - Provides extensive hardware monitoring via Shared Memory (SHM)
   - No need for RTSS (RivaTuner Statistics Server) as newer versions of HWiNFO have built-in FPS support
   - Considered the most comprehensive data source with best sensor coverage

2. **LibreHardwareMonitor**:
   - Built-in to InfoPanel, no additional software needed
   - Good alternative when you prefer not to install HWiNFO
   - May have less comprehensive sensor coverage than HWiNFO

3. **Plugins**:
   - For FPS monitoring without HWiNFO, you can use the [InfoPanel FPS Plugin](https://github.com/F3NN3X/InfoPanel.FPS)
   - Various other plugins exist for additional data sources

## Display Types Comparison

### Desktop Overlay
- **Pros**: No additional hardware required, infinitely customizable size
- **Cons**: Takes up screen real estate, subject to Windows display scaling issues

### HDMI/DP External Display
- **Pros**: Larger display options, standard connectivity
- **Cons**: Windows may treat it as a regular monitor (causing windowing issues), resolution management challenges, takes up a video output port

### USB LCD Panels
- **Pros**: Dedicated hardware, no impact on primary displays, no Windows resolution management issues
- **Cons**: Additional hardware cost, limited display sizes

## BeadaPanel Models

BeadaPanel offers several USB LCD panel models with excellent performance characteristics. These are generally the top recommendation for use with InfoPanel due to their superior performance and open communication protocol via WinUSB.

| Model | Resolution | Size | Performance | Notes |
|-------|------------|------|-------------|-------|
| BeadaPanel 2 | 480×480 | 53×53mm | >20 FPS | Square format |
| BeadaPanel 2W | 480×480 | 70×70mm | >20 FPS | Wider square format |
| BeadaPanel 3/3C | 320×480/480×320 | 40×62mm | >20 FPS | Portrait/Landscape variants |
| BeadaPanel 4/4C | 480×800/800×480 | 56×94mm | >20 FPS | Portrait/Landscape variants |
| BeadaPanel 5/5S/5C/5T | 800×480/854×480 | 108×65mm | >20 FPS | Multiple variants with slight differences |
| BeadaPanel 6/6C/6S | 480×1280/1280×480 | 60×161mm | >20 FPS | Portrait/Landscape variants |
| BeadaPanel 6P | 1280×480 | 6.8" | >20 FPS | Popular size with excellent performance |
| BeadaPanel 7/7C/7S | 1280×400/800×480 | Various | >20 FPS | Different aspect ratio options |

**Key BeadaPanel Features**:
- Dual communication channels (two "highways") for management and display
- Consistently high refresh rates (>20 FPS full screen updates)
- Better handling of sleep/wake cycles
- No "stuck" panel issues that require physical reconnection
- Generally the most reliable option
- Open SDK and documentation available
- Model 6P (6.8") offers excellent balance of size and performance

## Turing Smart Screen/TURZX Models

Turing Smart Screen (also known as TURZX) models are also supported by InfoPanel, though they have some limitations compared to BeadaPanel models. InfoPanel supports multiple Turing model types.

| Model | Resolution | Connection | Full Update Performance | Partial Update Performance | Notes |
|-------|------------|------------|--------------------|------------------------|-------|
| 8.8" Rev 1.1 | 480×1920 | WinUSB | 10 FPS | ~20 FPS | Better performance than Rev 1.0 |
| 8.8" Rev 1.0 | 480×1920 | Serial over USB | 1 FPS | ~10 FPS | Wake after sleep issues, slower performance |
| 5" Model | 800×480 | Serial over USB | 2-3 FPS | ~10 FPS | Wake after sleep issues |
| 3.5" Model | 480×320 | Serial over USB | 1 FPS | ~3 FPS | Wake after sleep issues, lower bandwidth |

**Limitations of Turing/TURZX Panels**:
- Single communication channel for both display and commands
- When the panel is "stuck," InfoPanel's restart attempts may not succeed because display and control commands use the same channel
- Serial models have issues with computer sleep/wake cycles
- Generally slower refresh rates than BeadaPanel options
- Closed protocol with no official documentation (InfoPanel is compatible at best effort basis)
- Turing's official software may provide better performance than third-party applications (e.g high FPS video backgrounds)

## Connection and Setup

> **Important Note**: Ensure no other software is actively using the panel when connecting with InfoPanel. Having multiple applications trying to control the display simultaneously can cause conflicts and unpredictable behavior.

### HWiNFO Setup

1. Install HWiNFO from [hwinfo.com](https://www.hwinfo.com/)
2. Enable Shared Memory Support:
   - Open HWiNFO
   - Go to Settings
   - Check "Minimize Main Window on Startup"
   - Check "Minimize Sensors on Startup"
   - Check "Minimize Sensors instead of closing"
   - Check "Auto Start"
   - Check "Shared Memory Support"

![HWINFO Settings](./Images/hwinfo-settings.png)

### InfoPanel Configuration

1. Launch InfoPanel
2. Go to Settings > Panels
3. Enable the appropriate panel type:
   - BeadaPanel
   - Turing Panel (8.8" Rev 1.1)
   - Turing Panel A (3.5")
   - Turing Panel C (5")
   - Turing Panel E (8.8" Rev 1.0)
4. Select a display profile for your panel
5. Adjust rotation and brightness as needed

## Troubleshooting

### Common Issues

#### Panel Appears "Stuck" or Frozen
- **For BeadaPanel**: In InfoPanel, try disabling and re-enabling the panel in Settings
- **For Turing/TURZX**: May require physical disconnection, as the command to restart uses the same communication channel that is likely blocked

#### Poor Performance
- Lower your target frame rate in InfoPanel settings
- Reduce the number of sensors being displayed
- Use simpler visualizations (text instead of gauges/graphs)
- For Turing panels, use more partial updates instead of full-screen changes

#### Panel Not Detected
- Try a different USB port
- Ensure you have the correct drivers installed
- Check your USB cable

## Recommendations

### Best Overall Panel: BeadaPanel
BeadaPanel is the recommended choice for use with InfoPanel due to:
- Faster refresh rates
- More reliable operation
- Better handling of sleep/wake cycles
- No "stuck" panel issues
- Dual communication channel architecture

### Panel Selection Guide

1. **For best performance**: BeadaPanel (any model, particularly the 6P 6.8" model)
2. **For large display with USB**: Turing/TURZX 8.8" Rev 1.1
3. **If already own a Turing/TURZX panel**: They work well with InfoPanel despite limitations

If choosing between USB and HDMI connections, USB panels are generally recommended as they don't have the Windows display management issues that can occur with HDMI connections.

---

Join the [InfoPanel Discord][discord] community for more help and to share your panel setups.

[discord]: https://discord.gg/cQnjdMC7Qc