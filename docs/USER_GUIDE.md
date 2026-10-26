# InfoPanel User Manual

InfoPanel is a free, open-source Windows application for displaying hardware monitoring data on desktop overlays and USB LCD panels. Create custom dashboards showing CPU temperatures, GPU usage, network speeds, and more, rendered on your desktop or directly on small LCD screens mounted on your PC.

**Download:** [GitHub Releases](https://github.com/habibrehmansg/infopanel/releases)
**Community:** [Discord](https://discord.gg/infopanel) | [Reddit](https://www.reddit.com/r/InfoPanel/)

---

## Table of Contents

1. [Getting Started](#getting-started)
2. [Profiles](#profiles)
3. [Design Page](#design-page)
4. [Display Items](#display-items)
5. [Sensor Sources](#sensor-sources)
6. [Charts and Graphs](#charts-and-graphs)
7. [Shapes](#shapes)
8. [Images](#images)
9. [USB Panels](#usb-panels)
10. [Plugins](#plugins)
11. [Global Hotkeys](#global-hotkeys)
12. [Settings](#settings)
13. [Tips and Tricks](#tips-and-tricks)
14. [Troubleshooting](#troubleshooting)

---

## Getting Started

### Installation

1. Download the latest installer from the [Releases page](https://github.com/habibrehmansg/infopanel/releases)
2. Run the installer and follow the prompts
3. InfoPanel runs as Administrator (required for hardware sensor access)

### First Launch

When you launch InfoPanel for the first time, it creates a default profile. The application has a sidebar with the following pages:

- **Home** -- Welcome page with quick links
- **Profiles** -- Create and manage display profiles
- **Design** -- Visual editor for building your dashboard
- **Plugins** -- Browse and install community plugins
- **USB Panels** -- Configure connected LCD panels
- **Settings** -- Application configuration
- **Logs** -- View application logs for troubleshooting
- **About** -- Links and credits

### Data Sources

InfoPanel can pull sensor data from three sources:

1. **HWiNFO** -- Run [HWiNFO](https://www.hwinfo.com/) with "Shared Memory Support" enabled in settings. This gives you access to hundreds of sensors.
2. **LibreHardwareMonitor** -- Built-in sensor polling (enable in Settings). No external software needed.
3. **Plugins** -- Community plugins that provide additional data (weather, audio, network, etc.)

---

## Profiles

A profile is a canvas where you place display items. Each profile has its own dimensions, background, and collection of items. You can have multiple profiles and assign them to different desktop overlays or USB panels.

### Creating a Profile

1. Go to the **Profiles** page
2. Click **Add Profile**
3. Set a name, width, and height (in pixels)
4. Choose a background color
5. Click **Create**

### Profile Settings

Select a profile to see its settings:

| Setting | Description |
|---------|-------------|
| **Name** | Display name for the profile |
| **Width / Height** | Canvas size in pixels (up to 10000x10000) |
| **Background Color** | Canvas background color |
| **Active** | Enable/disable the desktop overlay for this profile |
| **Topmost** | Keep the overlay window above all other windows |
| **Show FPS** | Display frame rate on the overlay |
| **Font Scale** | Scale factor for all text (default 1.33) |
| **Default Font** | Default font family for new text items |
| **Default Color** | Default color for new text items |
| **OpenGL** | Enable GPU-accelerated rendering |
| **Start Minimized** | Don't show the overlay on startup |
| **Resize** | Allow resizing the overlay window |

### Import and Export

- **Export**: Right-click a profile and select Export. Saves as a `.infopanel` file including all assets.
- **Import**: Click Import and select a `.infopanel` file. You can also import `.sensorpanel` files from Aida64 (experimental).

### Undo and Redo

InfoPanel supports unlimited undo/redo for profile changes. Use **Ctrl+Z** to undo and **Ctrl+Y** to redo.

---

## Design Page

The Design page is where you build your dashboard layout. Select a profile from the dropdown at the top, then add and arrange display items.

### Adding Items

Use the toolbar buttons to add display items:
- **Text** -- Static text label
- **Sensor** -- Live sensor value
- **Clock** -- Current time
- **Calendar** -- Current date
- **Image** -- Local image file
- **Http Image** -- Image from a URL
- **Graph** -- Line or histogram chart
- **Bar** -- Bar chart
- **Donut** -- Circular progress indicator
- **Gauge** -- Custom gauge with needle
- **Shape** -- Geometric shape with gradients
- **Table** -- Tabular data from plugins
- **Group** -- Container to group items together

### Positioning Items

- **Drag** items to move them on the canvas
- Set exact **X/Y coordinates** in the properties panel
- Enable **Grid Snap** in Settings for alignment (configurable spacing)
- **Grid lines** appear when an item is selected (toggle in Settings)

### Selecting Items

- Click an item to select it
- **Ctrl+Click** to select multiple items
- Selected items show a colored border (color configurable in Settings)

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| **Ctrl+Z** | Undo |
| **Ctrl+Y** | Redo |
| **Ctrl+D** | Duplicate selected item |
| **Delete** | Delete selected item |
| **Arrow keys** | Nudge selected item by 1 pixel |

### Properties Panel

The right sidebar shows properties for the selected item. Properties vary by item type but common ones include:

- **Name** -- Item label (shown in the item list)
- **Position** -- X and Y coordinates
- **Rotation** -- 0-360 degrees
- **Lock** -- Prevent accidental movement
- **Hidden** -- Hide from display but keep in profile

---

## Step-by-Step: Mapping Sensors to a Design

This walkthrough shows how to take an existing profile (one you created or imported) and map hardware sensors to each display item.

### Understanding Sensor Bindings

When you import a profile or create one from a template, the sensor display items need to be connected to YOUR hardware's sensors. Each sensor item stores a reference to a specific sensor ID. Since sensor IDs differ between systems (different motherboards, GPUs, etc.), you need to remap them.

### Step 1: Open the Design Page

1. Click **Design** in the sidebar
2. Select your profile from the dropdown at the top
3. You'll see the canvas with all the display items laid out

### Step 2: Open the Sensor Browser

On the left side of the Design page, you'll see tabs for sensor sources:
- **HWiNFO** -- requires HWiNFO running with Shared Memory enabled
- **Libre** -- requires LibreHardwareMonitor enabled in Settings
- **Plugin** -- shows sensors from installed plugins

Click a tab to browse available sensors. They're organized in a tree structure:
- HWiNFO: Hardware component > Sensor category > Individual sensor
- Libre: Hardware component > Sensor type > Individual sensor

### Step 3: Identify Items That Need Mapping

Look at your profile. Sensor items that aren't mapped (or mapped to sensors that don't exist on your system) will show blank or "N/A".

Click on each sensor display item in the canvas or in the item list. The properties panel on the right shows:
- **Sensor Type**: HwInfo, Libre, or Plugin
- **Sensor**: The currently bound sensor (may show as empty or unrecognized)

### Step 4: Map Each Sensor Item

**Method A: Drag and Drop**

1. Find the sensor you want in the sensor browser (left panel)
2. Drag it directly onto an existing sensor item on the canvas
3. The item updates to show the new sensor's value

**Method B: Properties Panel**

1. Click the sensor display item on the canvas to select it
2. In the properties panel (right side), find the sensor binding section
3. Click the sensor picker to browse and select a new sensor
4. The item immediately updates with the new sensor's value

### Step 5: Verify Each Mapping

After mapping a sensor:
- The display item should show a live value (not blank or N/A)
- Check the unit is correct (C, %, MHz, etc.)
- Verify the value makes sense (CPU temp should be 30-90C, not 0 or 999)

### Step 6: Adjust Value Display

For each sensor item, you can customize how the value appears:

1. **Value Type**: Choose NOW (current reading), MIN, MAX, or AVG
2. **Show Name**: Toggle to display the sensor name alongside the value
3. **Show Unit**: Toggle to append the unit (C, %, W, etc.)
4. **Thousands Separator**: Toggle comma grouping for large numbers
5. **Override Precision**: Set decimal places (0 = whole numbers, 1 = one decimal, etc.)
6. **Override Unit**: Replace the default unit with custom text
7. **Threshold Colors**: Set color zones based on value ranges

### Step 7: Map Chart/Graph Sensors

Charts (Graph, Bar, Donut) also need sensor bindings:

1. Click the chart item on the canvas
2. In properties, find the sensor binding section
3. Select the sensor source and specific sensor
4. Set Min/Max values (or leave as Auto)
5. The chart starts plotting live data immediately

### Step 8: Map Image Sensors

Http Image and Sensor Image items may reference plugin sensors:

1. Click the image item
2. In properties, set the sensor type to Plugin
3. Select the plugin and sensor that provides the image URL or data
4. The image updates with the plugin's output

### Example: Mapping a CPU Temperature Sensor

1. Click a sensor display item that should show CPU temperature
2. In the sensor browser, expand **HWiNFO** (or **Libre**)
3. Navigate to your CPU (e.g. "Intel Core i9-14900K")
4. Find "CPU Package" under the Temperature category
5. Click it (or drag it onto the item)
6. The item now shows your CPU temperature
7. Optional: Set threshold colors (green < 60, yellow < 80, red >= 80)

### Example: Mapping GPU Usage

1. Click a bar or donut chart that should show GPU load
2. In the sensor browser, navigate to your GPU
3. Find "GPU Core Load" (shows as percentage)
4. Select it
5. Set Min=0, Max=100
6. The chart now shows live GPU usage

### Common Sensor Mappings

| What to show | Where to find it |
|---|---|
| CPU Temperature | CPU > Temperatures > CPU Package |
| CPU Usage | CPU > Load > CPU Total |
| GPU Temperature | GPU > Temperatures > GPU Core |
| GPU Usage | GPU > Load > GPU Core |
| RAM Usage | Memory > Load > Memory Used |
| Fan Speeds | Fans > Fan #1, #2, etc. |
| Disk Temperature | Storage > Temperatures > Drive Temperature |
| Network Speed | Network > Throughput > Download/Upload Rate |
| CPU Clock | CPU > Clocks > CPU Core #0 |
| GPU Clock | GPU > Clocks > GPU Core |
| Power Draw | CPU/GPU > Powers > CPU/GPU Package Power |

### Tip: Batch Remapping

If you imported a profile made for different hardware, you may need to remap many sensors at once. Work through them systematically:

1. Start from the top-left of your design
2. Click each sensor item
3. Check if it shows a valid value
4. If blank/N/A, remap it to the equivalent sensor on your hardware
5. Move to the next item

Sensor items that show values correctly don't need remapping, as the sensor IDs matched between systems.

---

## Display Items

### Text

A static text label. Use it for titles, labels, or any fixed text.

**Properties:**
- Font family, size, bold, italic, underline, strikeout
- Color with full color picker
- Alignment: left, center, right
- Uppercase toggle
- Text wrapping (set a width to enable)
- Marquee scrolling (scrolling text, with speed and spacing controls)

### Sensor

Displays a live hardware sensor value. This is the core building block of InfoPanel dashboards.

**Properties (in addition to text properties):**
- **Sensor source** -- HwInfo, LibreHardwareMonitor, or Plugin
- **Value type** -- NOW (current), MIN, MAX, or AVG
- **Show name** -- Display the sensor name alongside the value
- **Show unit** -- Display the unit (C, %, MHz, etc.)
- **Thousands separator** -- Format large numbers with commas (e.g. 1,234)
- **Override precision** -- Control decimal places (0-3)
- **Override unit** -- Display a custom unit string
- **Threshold coloring** -- Set 3 color zones with thresholds (e.g. green below 60C, yellow 60-80C, red above 80C)
- **Multiplier / Division** -- Scale the sensor value
- **Addition modifier** -- Add/subtract a fixed offset

### Clock

Displays the current time with a customizable format string.

**Format examples:**
- `hh:mm:ss tt` -- 02:30:45 PM
- `HH:mm` -- 14:30
- `hh:mm` -- 02:30

### Calendar

Displays the current date with a customizable format string.

**Format examples:**
- `dd/MM/yyyy` -- 19/03/2026
- `MMMM dd, yyyy` -- March 19, 2026
- `ddd, MMM d` -- Thu, Mar 19

### Table

Displays tabular data from plugin sensors in rows and columns.

**Properties:**
- Max rows (limit visible rows)
- Show header row
- Column formatting
- Plugin sensor source

---

## Sensor Sources

### HWiNFO

1. Install and run [HWiNFO](https://www.hwinfo.com/)
2. In HWiNFO settings, enable **Shared Memory Support**
3. Start HWiNFO sensors
4. In InfoPanel Design page, the HWiNFO tab shows all available sensors organized in a tree

### LibreHardwareMonitor

1. Go to InfoPanel **Settings**
2. Enable **LibreHardwareMonitor**
3. Install the PawniO driver if prompted (required for low-level hardware access)
4. Sensors appear in the Libre tab on the Design page

### Plugin Sensors

1. Install plugins from the **Plugins** page
2. Plugin sensors appear in the Plugins tab on the Design page
3. Drag a sensor onto the canvas to create a display item

### Adding a Sensor to Your Profile

1. Go to the **Design** page
2. Select your profile from the dropdown
3. Open the sensor browser on the left (HWiNFO, Libre, or Plugin tab)
4. Browse or search for the sensor you want
5. Click the sensor to add it to the canvas
6. Position and style it using the properties panel

---

## Charts and Graphs

### Graph (Line/Histogram)

Displays sensor data over time as a line chart or histogram.

**Properties:**
- **Type** -- Line or Histogram
- **Line thickness** -- For line charts
- **Step** -- Sampling interval
- **Fill** -- Fill area under the line (with custom color)
- **Frame** -- Border around the chart
- **Background** -- Background color behind the chart
- **Min/Max** -- Auto or manual value range
- **Colors** -- Line color, fill color, frame color, background color

### Bar

A horizontal or vertical bar showing a sensor value as a percentage.

**Properties:**
- Same as Graph (colors, frame, background, min/max)
- Useful for showing CPU/GPU usage, temperatures, etc.

### Donut

A circular progress ring showing a sensor value as a percentage.

**Properties:**
- Inner/outer radius
- Start/end angle
- Colors for the progress arc and background
- Sensor source and value range

### Gauge

A custom gauge with a needle indicator. Uses an image as the gauge face and overlays a needle based on sensor value.

**Properties:**
- Gauge image (the background face)
- Needle configuration
- Min/Max values
- Sensor source
- Smooth animation

---

## Shapes

Shapes are decorative elements for building your dashboard layout.

### Available Shapes

Rectangle, Capsule, Trapezoid, Parallelogram, Ellipse, Triangle, Pentagon, Hexagon, Octagon, Star, Plus, Arrow

### Shape Properties

- **Fill** -- Solid color fill
- **Frame** -- Border with customizable color and thickness
- **Gradient** -- Two-color gradient with angle control and animation speed
- **Corner radius** -- Round the corners (mainly for rectangles)
- **Width / Height** -- Dimensions

Shapes are great for creating backgrounds, dividers, progress bar frames, and decorative elements.

---

## Images

### Local Image

Display a local image file (PNG, JPG, GIF, SVG, WebP).

1. Add an **Image** item
2. Browse to your image file
3. Animated GIFs and WebP files play automatically
4. SVG images render at any resolution without pixelation

### HTTP Image

Display an image from a URL that updates automatically.

1. Add an **Http Image** item
2. Enter the URL
3. The image refreshes based on cache settings

### Plugin Image

Display a real-time image from a plugin (e.g. audio spectrum visualizer).

1. Install a plugin that provides images (e.g. AudioSpectrum)
2. Add an image item and select the plugin image source
3. The image updates in real-time via shared memory

### Video / Live Stream

InfoPanel supports video files and live streams.

1. Add an **Image** item
2. Set the source to a video file (.mp4, .avi, .mkv) or stream URL (.m3u8, rtsp://)
3. Videos loop automatically
4. Volume control available in properties

---

## USB Panels

InfoPanel can render directly to USB LCD panels mounted on CPU coolers, cases, or desks.

### Supported Panel Types

**BeadaPanel:**
- Various sizes and resolutions
- Brightness control, rotation

**Turing (Turzx) Smart Screen:**
- 3.5", 5", 8.8", and other sizes
- JPEG streaming, storage management
- Per-device target FPS and quality settings

**Thermalright Panels:**
- ChiZhu/SSCRM panels (Grand Vision, Wonder Vision, etc.)
- Trofeo Vision HID and bulk panels
- SCSI panels (Elite Vision, Frozen Warframe)
- 40+ models auto-detected by PM byte
- Display mask for Vision 360 camera punch-hole
- Flicker fix for Trofeo 9.16"
- Software brightness, JPEG quality, target FPS

### Setting Up a USB Panel

1. Connect the USB panel to your PC
2. Go to the **USB Panels** page
3. Enable the panel type (BeadaPanel, Turing, or Thermalright)
4. Click **Discover** to find connected devices
5. Select a profile to display on the panel
6. Enable the device

### Per-Device Settings

Each device can have:
- **Profile** -- Which profile to display
- **Rotation** -- 0, 90, 180, 270 degrees
- **Brightness** -- 0-100%
- **Target FPS** -- 1-30 frames per second
- **JPEG Quality** -- 50-100% (affects image quality vs. transfer speed)

---

## Plugins

Plugins extend InfoPanel with additional data sources and features.

### Installing Plugins

**From the Plugin Browser:**
1. Go to the **Plugins** page
2. Browse available plugins
3. Click Install

**Manually:**
1. Download the plugin ZIP
2. Extract to `C:\ProgramData\InfoPanel\plugins\YourPlugin\`
3. Restart InfoPanel

### Bundled Plugins

InfoPanel includes built-in plugins:
- **System Info** -- CPU, RAM, OS details
- **Network Info** -- Network adapter stats
- **Drive Info** -- Storage space monitoring
- **Volume** -- System volume level
- **Weather** -- Weather data (via OpenWeatherMap)

### Plugin Configuration

Some plugins have configurable settings:
1. Go to the **Plugins** page
2. Click on a plugin to expand its settings
3. Adjust settings in real-time (changes apply immediately)

---

## Global Hotkeys

Assign keyboard shortcuts to switch panel profiles on the fly.

### Setting Up Hotkeys

1. Go to the **USB Panels** page
2. Scroll to the **Global Hotkeys** section
3. Click the hotkey capture field and press your key combo (e.g. Ctrl+Shift+1)
4. Select the target panel
5. Select the profile to switch to
6. Click **Add**

### Managing Hotkeys

- **Edit** -- Click the edit button to modify a hotkey (the original is preserved until you confirm)
- **Remove** -- Click the delete button to remove a hotkey
- Duplicate key combos are detected and blocked

---

## Settings

### Application

| Setting | Description |
|---------|-------------|
| **Theme** | Light or Dark mode |
| **Run on startup** | Auto-start with Windows |
| **Startup delay** | Wait before starting (seconds) |
| **Autosave** | Automatically save profile changes |
| **Minimize to tray** | Minimize to system tray instead of taskbar |
| **Start minimized** | Start hidden in system tray |
| **Close to minimize** | Close button minimizes instead of exiting |

### Panel

| Setting | Description |
|---------|-------------|
| **Selected item color** | Highlight color for selected items in the Design page |
| **Show grid lines** | Show alignment grid when items are selected |
| **Grid spacing** | Distance between grid lines (5-200px) |

### LibreHardwareMonitor

| Setting | Description |
|---------|-------------|
| **Enable** | Turn on built-in hardware monitoring |
| **Storage monitoring** | Monitor disk drives |
| **Storage polling interval** | How often to check disk stats (1-300 seconds) |

### Performance

| Setting | Description |
|---------|-------------|
| **Target frame rate** | Maximum FPS for desktop overlays (1-60) |

---

## Tips and Tricks

### Building an Effective Dashboard

1. **Start with a plan** -- Sketch your layout before building. Know what sensors matter to you.
2. **Use shapes for structure** -- Create background panels and dividers with shapes before adding sensors.
3. **Group related items** -- Use the Group feature to organize sections. This makes it easy to move entire sections.
4. **Use threshold coloring** -- Set color thresholds on sensors so high temperatures turn red automatically.
5. **Match your panel resolution** -- Set the profile dimensions to match your USB panel's resolution exactly.

### Performance Tips

1. **Lower FPS for USB panels** -- USB panels don't need 60fps. 15-20fps is usually smooth enough.
2. **Reduce JPEG quality** -- For USB panels, quality 85-90 is visually identical to 100 but much faster.
3. **Use LibreHardwareMonitor** -- It's built-in and doesn't require running a separate application.
4. **Disable unused profiles** -- Inactive profiles don't consume resources.

### Sharing Profiles

1. Export your profile from the Profiles page
2. Share the `.infopanel` file
3. Others can import it and adjust sensor bindings for their hardware

---

## Troubleshooting

### Sensors Not Showing

- **HWiNFO**: Make sure HWiNFO is running with "Shared Memory Support" enabled
- **LibreHardwareMonitor**: Enable it in Settings. Install PawniO driver if prompted.
- **Plugins**: Check the Plugins page to make sure plugins are loaded and enabled

### USB Panel Not Detected

1. Check USB connection
2. Click **Discover** on the USB Panels page
3. For Thermalright panels: ensure the correct USB driver is installed (WinUSB for bulk devices)
4. Check the **Logs** page for error messages

### Display Overlay Not Showing

1. Make sure the profile is set to **Active**
2. Check that the profile has display items
3. Verify the overlay window isn't positioned off-screen (reset X/Y to 0)

### Poor Performance

1. Lower the target frame rate in Settings
2. Disable OpenGL if it causes issues
3. Reduce the number of active profiles
4. For USB panels, lower JPEG quality and target FPS

### Application Logs

Go to the **Logs** page to view real-time application logs. Use the **Copy to Clipboard** button to share logs when reporting issues. Logs are stored in `%LOCALAPPDATA%\InfoPanel\logs\`.

---

## Data Storage

All user data is stored locally:

| Path | Contents |
|------|----------|
| `%LOCALAPPDATA%\InfoPanel\settings.xml` | Application settings |
| `%LOCALAPPDATA%\InfoPanel\profiles.xml` | Profile list |
| `%LOCALAPPDATA%\InfoPanel\profiles\` | Individual profile data |
| `%LOCALAPPDATA%\InfoPanel\assets\` | Profile images and assets |
| `%LOCALAPPDATA%\InfoPanel\logs\` | Application logs (7-day retention) |
| `%ProgramData%\InfoPanel\plugins\` | Installed plugins |

---

## Support

- **GitHub Issues**: [Report bugs](https://github.com/habibrehmansg/infopanel/issues)
- **Discord**: Join the community for help and sharing
- **Reddit**: [r/InfoPanel](https://www.reddit.com/r/InfoPanel/)

If you find InfoPanel useful, consider supporting the project:
- [Buy Me a Coffee](https://buymeacoffee.com/habibrehman)
- Leave a review on GitHub
