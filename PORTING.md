# Porting backlog: 1.x → 2.0

`2.0.x` removed the 1.x WPF app (`InfoPanel/`, `InfoPanel.Views.Skia/`, `InnoSetup/`). The portable model and renderer moved to `InfoPanel.Core` with history (`git log --follow`). Everything else is listed here with where it went or goes in 2.0: "done" when 2.0 has it, "later" when it is still to come. Read the 1.x source with `git show 1.4.x:<path>` (for example `git show 1.4.x:InfoPanel/HWHash/HWHash.cs`).

2.0 draws through the scene runtime (`InfoPanel.Core/Scenes/`), with sensor sources (`ISensorSource`), media providers (`IMediaProvider`) and outputs (`IFrameSink`, `InfoPanel.Core/Output/`). The 1.x renderer (`InfoPanel.Core/Drawing/`, with `IImageSource` and `IMediaPlayback`) is kept only as the parity oracle for the goldens.

## Sensors

| 1.4.x component | 2.0 target | Source (`git show 1.4.x:<path>`) |
|---|---|---|
| HWHash (HWiNFO shared memory) | done: the `hwinfo` source (`InfoPanel.Core/Sensors/HwInfo/`), local and remotes under 1.x's keys, HWiNFO's own statistics; the connection picker and type groups later | `InfoPanel/HWHash/HWHash.cs` |
| LibreMonitor | done: LibreHardwareMonitorLib 0.9.6 runs out of process in `InfoPanel.SensorHelper`, serving a closed, read-only protocol over a named pipe (`InfoPanel.Core/SensorHelper/`): 1.x's groups, identifiers as LHM writes them, 1.x's order and units, Throughput labelled B/s (1.x: KB/s, the same number), null kept as null; the `libre` source (`InfoPanel.Core/Sensors/Libre/`) starts it from `sensor-helper/` beside the engine, reads it every 2 s under 1.x's `libre:{Identifier}` keys with sub-hardware folded into its parent, applies `sources.libre.*` live and restarts it with backoff; three 1.x bugs fixed (below); the PawnIO driver and "Read all sensors" done (the next two rows) | `InfoPanel/Monitors/LibreMonitor.cs`, `InfoPanel/Models/SensorReader.cs` |
| PawnIO driver setup | done (the driver; its administrator rights in the row below): the sensor helper reads 1.x's registry value (`DisplayVersion` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO`, 64-bit view, unelevated; minimum 2.0.0.0) at its start and says `missing`, `outdated` or `installed` in `hello`, `notLoaded` for one installed when it runs without administrator rights (LibreHardwareMonitorLib can't open PawnIO without them); the Libre source is `limited` · `noDriver` / `driverOutdated` (`notElevated` / `driverNotLoaded` for a helper that says `notLoaded`, without administrator rights or with them), and the engine reads the value every 10 s and starts a new helper when it changes. No start-up prompt (1.x asked at every start): a client installs it on request (`POST /api/sensors/sources/libre/driver`), which runs PawnIO 2.2.0's installer from `pawnio/` beside the engine (not embedded or committed: the Windows package carries it, its SHA-256 pinned) with `-install -silent` as `runas`; outcomes installed, restart needed, declined, failed; after installed the driver is `installing` until the new helper said hello. Never uninstalled | `InfoPanel/Utils/PawnIoHelper.cs`, `InfoPanel/Resources/PawnIO_setup.exe` |
| Running as administrator (`requireAdministrator`) | done differently: the engine never runs elevated and never prompts by itself. Libre reads every sensor through "Read all sensors" (`POST`/`DELETE /api/sensors/sources/libre/elevation`, session only): this install's sensor helper, run once with `runas` as `--register --user <the engine's user SID>` (one Windows prompt; an administrator approves it for a standard user), creates `\InfoPanel 2\Sensor helper <SID>`, a task run as SYSTEM (never under the user's token, whose environment the user can write) with no trigger, no time limit, on battery, one instance, the user allowed only to read and run it; its action is the helper under Program Files (`--register` refuses one anywhere else, and the engine offers nothing then: `unavailable` · `notInstalled`), `--serve --user <SID>`. The unelevated engine starts it with `schtasks /Run` at each helper start, opens its pipe (named for the SID, owned by SYSTEM, open to that user only, first instance, no network logon) and checks its owner is SYSTEM before sending anything, and that its helper answers its hello; otherwise, or after 10 s, it runs its own child as before and logs why. `--register` sets the task's and its folder's security every time. `--unregister` removes it, and the folder once empty (the uninstaller's call). The helper is a Windows-subsystem program with startup hooks off. 1.x ran the whole app as administrator (a manifest prompt at each start, or its autostart task at the highest run level) | `InfoPanel/Resources/app.manifest`, `InfoPanel/Models/ConfigModel.cs` |
| PluginMonitor + proxies | done for sensors: the `plugins` source and the hosts' manager (`InfoPanel/Plugins/`, docs/2.0/plugins.md); plugin images, actions and settings later | `InfoPanel/Monitors/PluginMonitor.cs`, `InfoPanel/Monitors/PluginHostConnection.cs`, `InfoPanel/Monitors/PluginProcessManager.cs`, `InfoPanel/Monitors/PluginProxies/*.cs` |
| SensorReader (static reads, missing reading → null) | done: the sensor hub (`SensorHub`); stale, unavailable and denied are distinct statuses (`SensorStatus`); Windows sources build on `PolledSensorSource` | `InfoPanel/Models/SensorReader.cs` |
| GraphDraw's lazy history queues (sampled at `TargetGraphUpdateRate`) | done: the sensor history (`SensorHistory`, `ISensorHistory`), which the scene runtime's series read | `InfoPanel/Drawing/GraphDraw.cs` |
| HWiNFO smart import (remap sensors by name) | done: the 1.x import binds a `.infopanel` file's HWiNFO items by name (spec/docs/import.md, "HWiNFO sensors matched by name") | `InfoPanel/Models/SharedModel.cs` (~l.900, "Smart imported") |

## Images and video

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| `LockedImage` FFMPEG branch (Flyleaf player, snapshots via WPF `BitmapSource`), `VideoBackgroundHelper`, the `ImageDisplayItem.Volume` handling | done: video files decoded by FFmpeg in the scene runtime (`InfoPanel.Core/Images/Video/`, `VideoPlayer`; spec/docs/smart-nodes.md, "Video files"); sound is never played | `InfoPanel/Models/LockedImage.cs`, `InfoPanel/Models/VideoBackgroundHelper.cs` |
| `LockedImage` HTTP branch (download on load) | done: web images by address (`HttpMediaProvider`, spec/docs/providers.md), with MJPEG and RTSP camera streams | `InfoPanel/Models/LockedImage.cs` |
| `LockedImage` PLUGIN branch, `Cache.GetPluginImage` / `InvalidatePluginImages` | later: plugin images in the scene runtime (the named `MemoryMappedFile` transport is Windows-only) | `InfoPanel/Models/LockedImage.cs`, `InfoPanel/Utils/Cache.cs`, `InfoPanel/Monitors/PluginProxies/ProxyPluginImage.cs` |
| `LockedImage` GPU texture frame cache (`GRContext`, dispatcher disposal) | later: a GPU-backed renderer, if ever needed | `InfoPanel/Models/LockedImage.cs`, `InfoPanel/Drawing/AcceleratedGraphics.cs` |
| `Cache.InvalidateImage` on URL edits | done differently: 2.0 documents name uploaded media by content (`sha256:` ids, `blobs/`), and an item whose reference changes takes the new one (spec/docs/smart-nodes.md, "Media held across a change of reference") | `InfoPanel/Utils/Cache.cs`, `InfoPanel/Models/ImageDisplayItem.cs` |

## Output: panels and overlays

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| BeadaPanel, Turing, Thermalright and Thermaltake tasks and protocols | BeadaPanel done: driven from `devices.json` as an `IFrameSink` (`InfoPanel.Core/Panels/`: libusb-1.0, the protocol byte for byte, the manager and the panel sink; docs/2.0/panels.md); Turing, Thermalright and Thermaltake later | `InfoPanel/Services/{BeadaPanel,BeadaPanelDevice,TuringPanel,TuringPanelSerial,TuringPanelUsbDevice,ThermalrightPanel,ThermalrightPanelDevice,ThermaltakePanel,ThermaltakePanelDevice}Task.cs`, `InfoPanel/BeadaPanel/`, `InfoPanel/TuringPanel/`, `InfoPanel/ThermalrightPanel/`, `InfoPanel/ThermaltakePanel/`, `InfoPanel/Models/*PanelDevice.cs`, `InfoPanel/Lib/` (Turing DLLs) |
| `SKBitmapExtensions.EnsureBitmapSize` and the LCD rotation helpers (`GetRotatedDimensions`, `GetRotationAngle`, `LCD_ROTATION`) | done: `PanelTransform` (rotation, fit with 1.4.x's integer offsets, RGB565) after the render loop, on the panel's thread; `FrameRequest` stays scale and rate | `InfoPanel/Extensions/SKBitmapExtensions.cs`, `InfoPanel/ViewModels/UsbPanelsViewModel.cs` (`LCD_ROTATION`), `InfoPanel/Enums/Rotation.cs` |
| `SKBitmapComparison` (skip unchanged panel frames) | done: the render loop hashes each frame and hands a sink only one it doesn't have (`RenderLoop`) | `InfoPanel/Extensions/SKBitmapComparison.cs` |
| `PanelDrawTask` (per-panel render loop) | done: `RenderService`'s render loops over the scene runtime (`InfoPanel.Core/Output/`); a panel joins them as an `IFrameSink` (`PanelSink`), its writes on its own thread (`PanelRunner`) with 1.4.x's pacing | `InfoPanel/Services/PanelDrawTask.cs` |
| DisplayWindow*, `DisplayWindowManager`, ForegroundAppMonitor, ScreenHelper | macOS and Windows done: `InfoPanel/Desktop/` (`DisplayWindowManager`, an `IFrameSink` per active profile, AppKit in `Desktop/MacOS/`, Win32 in `Desktop/Windows/`: each display's device name as its `name`, the name Windows shows people as its `label`, physical pixels as 1.x). Still to port: ForegroundAppMonitor (`TriggerProcessNames`). The profile page's display picker and `Fullscreen()` are a client's now: `GET /api/displays` lists the displays, each in the shape `window.display` takes, and filling one is a single `profile` commit (`spec/docs/api.md`, "Displays") | `InfoPanel/Views/DisplayWindow.xaml.cs`, `InfoPanel/Views/SkiaDisplayWindow.xaml.cs`, `InfoPanel/DisplayWindowManager.cs`, `InfoPanel/DisplayWindowThread.cs`, `InfoPanel/Services/ForegroundAppMonitor.cs`, `InfoPanel/Utils/ForegroundWindowHelper.cs`, `InfoPanel/Utils/ScreenHelper.cs` |
| Selection blink, grid lines and the selected-item colour drawn into outputs (`Settings.ShowGridLines`, `GridLinesSpacing`, `GridLinesColor`, `SelectedItemColor`) | Not ported to Core: engine output has no selection or grid lines | `InfoPanel/Drawing/PanelDraw.cs`, `InfoPanel/Models/Settings.cs` |
| `ProfilePreviewCoordinator` (batched thumbnails) | done: `RenderService.RenderOnce` and `GET /api/profiles/{id}/thumbnail` | `InfoPanel/Services/ProfilePreviewCoordinator.cs` |

## Persistence and editing

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| `SharedModel` item XML load/save, `.infopanel` import/export | Read: `ProfileSerializer` (through `SafeXml`); `.infopanel` import and the 1.x folder: the 1.x import (`InfoPanel.Core/Import/`: `V1Reader`, `V1Folder`, `ArchiveReader`), which reads into an envelope through `/api/import/v1` (spec `docs/import.md`) and installs nothing; a client creates the profiles. `InfoPanelArchive.Export` writes only the e2e archive. Export: a 2.0 profile as a profile archive, `.infopanel2` (`ProfileArchive`, `GET /api/profiles/{id}/archive`, spec `docs/archive.md`), named by 1.4.x's file-name rule, opened again with `POST /api/import/archive` | `InfoPanel/Models/SharedModel.cs` |
| `ConfigModel` profile list and settings (`settings.xml`, its `.bak` fallback) | Read by the 1.x import (`V1Folder`, `V1SettingsReader`: the profile list, the settings' user data); applying them is a client's, and the run-once record says whether it did (spec/docs/import.md, "Settings") | `InfoPanel/Models/ConfigModel.cs`, `InfoPanel/Models/Settings.cs` |
| `UndoManager` | done: each document's undo and redo history, bounded in bytes (`InfoPanel.Core/Documents/History.cs`, `ProfileDocument.Undo` and `Redo`) | `InfoPanel/Services/UndoManager.cs` |
| 1.4.x model changes after `8a3062d`: `GuideDisplayItem` (15th item type, editor alignment guides) and `ImageDisplayItem.RefreshInterval` | done: guides read from the raw XML by the 1.x import (the envelope's `guides`); `RefreshInterval` read into the envelope's `meta.refreshInterval`, which a client carries into a provider source's `refresh` (spec/docs/providers.md, "Refresh") | `InfoPanel/Models/GuideDisplayItem.cs`, `InfoPanel/Models/ImageDisplayItem.cs` |
| `FontCache` (system font list for pickers) | done: `FontRegistry` and `GET /api/fonts` | `InfoPanel/Utils/FontCache.cs` |

## 1.4.x commits after `8a3062d` still to port

`8a3062d` is the merge base with `2.0.x` (tip of `1.4.x` when the 2.0 line branched; `1.4.x` is now at `c71f0f0`). Three of the changes made since then applied to `2.0.x` as they were and are ported: the OBS Monitor entry in `plugins.json`, the Weather plugin's Metric/Imperial option (with `PluginSensor.Unit`'s setter) and the plugin host's table-mutation fix in `SensorSnapshotManager`. The 23 commits below touch code that isn't on `2.0.x` yet, so the task that brings each area over ports its changes from the `1.4.x` tip, not from `8a3062d`: read a file as it is there with `git show origin/1.4.x:<path>`, and `git show <sha>` for the change and its reason. Each task adds a test that pins the behaviour it carries over; `1.4.x` has none.

Not ported, on purpose: changes to the WPF windows and pages only (the User Guide tile on the home page, the profile display-assignment dropdown, the editor's multi-select and drag handling), and the Lian Li 8.8 inch panel commit `ec6695d` with its revert `4528850`, which cancel out.

### USB panel families (output panels, `IFrameSink`)

Port the protocol and device code (`JlPanel/*`, `VmaxPanel/*`, `JonsboPanel/*`, `LianLiPanel/*` and the Thermalright model tables); the settings, device lists and hotkey wiring in these commits are WPF and are rewritten against the engine's settings. Land each family's fixes with it: a family ported from the tip already has them.

| Family | Commit | What it does |
|---|---|---|
| Jungle Leopard / Hongtai cooler display | `39ac3d4` | New USB serial panel family and its protocol notes (`JL-PROTOCOL.md`, `PANELS.md`) |
| Thermalright Elite Vision 360 ARGB Black (SPISCRM-V2) | `23d60fb` | New model |
| | `5a8bd39` | Fix: SPISCRM-V2 identifier parsing, frame type and RGB565 (the panel showed a wrong image) |
| Thermalright detection | `f7ca672` | Fix: three-pass ChiZhu detection so panels identified by PM stay intact |
| Thermalright Trofeo Vision | `4d6b2d4` | New model: 11.3 inch (1920x400) |
| | `689ee3e` | Probes TrofeoBulk byte 20 to tell the 11.3 inch from the 9.16 inch |
| | `6b23229` | 9.16 inch v2 renders at 480 |
| | `315ab6f` | Fix: caps HID Trofeo JPEG frames at 400 KB (the 6.86 inch froze at high brightness) |
| Thermalright Wonder Vision | `d19cc5e` | New model: 360 v2 (SSCRM-V3, SUB=0x20) |
| VMAX 4.6 inch | `793c03a` | New direct USB family |
| | `3d24d42` | Fix: colour channel order (BGR) |
| | `0b43898` | Streaming optimisation, capped at 30 fps |
| | `0a5c26a` | Removes debug scaffolding |
| | `80824a7` | Discovery aligned with the other panel types |
| Jonsbo (DS916, DS339) | `8a617cb` | New USB family |
| | `a2c8ed0` | Fix: DS339 bulk write timeouts on start-up |
| | `0f81b8f` | Drives MS9132 panels at the resolution their EDID reports |
| | `3939e56` | Logs the full MS9132 EDID block |
| Lian Li LCD | `c3306ca` | New USB family (distinct from the reverted 8.8 inch commit) |

### Plugin host

The host runs as a mode of the engine (`--plugin-host`) and the app side's sensors are ported (`InfoPanel/Plugins/`); these two touch what is still to come: actions, with a plugin settings and actions API, and plugin images (see the Images table above).

| Commit | What it does |
|---|---|
| `48b3684` | Fix: plugin actions are invoked through the current host connection, so the action buttons keep working after the host restarts (`PluginsViewModel`; port the idea with actions: 2.0's hosts restart in `PluginHostRunner`). Ported for panel hosts: a hosted panel's every call goes through its host's connection as it is then, so after a restart the panel's next session and its writes reach the new host, never the ended one (`InfoPanel.Core/Panels/Hosted/HostedPanelDriver.cs`; pinned by `HostedPanelTests.After_its_host_restarts_the_proxy_goes_through_the_new_connection_never_the_stale_one`) |
| `1acc815` | Fix: large plugin images (2650x720) starved the window's input under heavy render load. Adds a no-resize fast path for plugin frames in `LockedImage` (port with plugin images); the `DisplayWindow` dispatcher-priority part is WPF only |

### Guide and image-refresh model fields

Both are done (the Persistence table above): the 1.x import reads guides from the raw XML and an image item's `RefreshInterval`, and web images refresh as their source's `refresh` says.

| Commit | What it does |
|---|---|
| `4c82f71` | `GuideDisplayItem`, a 15th item kind for editor alignment guides (the same commit's editor changes are WPF only; `2.0.x` documents already have `Guides`) |
| `1114dac` | `ImageDisplayItem.RefreshInterval`: re-download URL images every N seconds, with `LockedImage.LoadedAtUtc` and the cache logic in `Utils/Cache.cs` |

## Later

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| WebServerTask (HTTP API, `/{id}/image`) | engine host, later | `InfoPanel/Services/WebServerTask.cs` |
| Global hotkeys | later | `InfoPanel/Services/GlobalHotkeyService.cs`, `InfoPanel/Models/HotkeyBinding.cs` |
| Tray icon | the engine process's own menu (docs/2.0/desktop-shell.md): macOS done (the menu-bar item: status line, Open Editor, Quit with the unsaved-changes alert); Windows done (the notification-area icon with the same menu in Windows' words, `InfoPanel/Shell/Windows/`); Linux later | `InfoPanel/Views/Windows/MainWindow.xaml`, `InfoPanel/Views/Windows/MainWindow.xaml.cs` |
| Autostart (Task Scheduler) | Windows: done, the tray menu's Start when I sign in, the Run value `InfoPanel 2` (`InfoPanel/Shell/Windows/WinStartup.cs`, docs/2.0/desktop-shell.md; the uninstaller removes it), not a scheduled task, as the engine never runs as administrator; macOS and Linux later | `InfoPanel/Models/ConfigModel.cs` |
| UpdateChecker and toasts | the check: done, without the machine name 1.x sent (`InfoPanel.Core/Updates/`: GitHub's public releases 30 s after the menu-bar item shows and daily; the menu's Update Available and Check for Updates… lines, docs/2.0/desktop-shell.md "Updates"); the OS notification: done, once per new version, ever (a balloon on the Windows tray icon, a macOS notification from the app; docs/2.0/desktop-shell.md "The update notification"); the Updates page's download and run: not ported (not decided) | `InfoPanel/Services/UpdateChecker.cs` |
| Sentry | later | `InfoPanel/App.xaml.cs` |
| AIDA64 SensorPanel import | later | `InfoPanel/Models/SharedModel.SensorPanelImport.cs` |
| ConfigModel / Settings (app settings) | the engine settings (`SettingsStore`, `GET`/`PATCH /api/settings`) hold the frame and graph rates, the log level, the sensor sources' settings and the others spec/docs/api.md lists ("Settings"); the devices', tray's, autostart's, hotkeys', autosave's and web server's settings later, each with its feature | `InfoPanel/Models/ConfigModel.cs`, `InfoPanel/Models/Settings.cs` |
| JobObject (plugin host lifetime) | later (Windows; elsewhere a host exits when its pipe ends, and the engine kills what is left when it stops) | `InfoPanel/Utils/JobObject.cs` |
| Installer | macOS: an app in a disk image, ad hoc signed, built by `packaging/macos/build.sh` (docs/2.0/packaging.md). Windows: done, `packaging/windows/` (`InfoPanel.iss`, built by `build.ps1`; docs/2.0/packaging.md): "InfoPanel 2" beside 1.x with its own AppId, folder, Start-menu group, Run value and task folder; per machine, x64; the licence page (`LICENSE`); the bundled PawnIO setup and, when no .NET 8 or later is installed, Microsoft's .NET runtime for the sensor helper, both hash-pinned; the finish page launches unelevated (1.x's ran as the elevated user); the uninstaller removes the Run value and the helper's tasks and keeps the data folder. Not 1.x's `[InstallDelete]` of the whole program folder's files | `InnoSetup/setup.iss` |

## 1.4.x bugs fixed in 2.0

Confirmed in 1.4.x's source; each fix is pinned by a test that fails with the 1.4.x behaviour.

| 1.4.x bug | 2.0 | Pinning test |
|---|---|---|
| A Libre sensor's average was always 0: `SensorReader.ReadLibreSensor` passed the literal `0` as the average | The engine keeps each Libre sensor's minimum, maximum and average over what it read | `LibreSourceTests.TheAverage_IsTheEnginesOwn_OverTheSamplesRead_NotOneFourXsZero` |
| A Libre sensor with no value read as 0: `sensor.Value ?? 0` (and `Min ?? 0`, `Max ?? 0`) in `SensorReader.ReadLibreSensor` | A null value is a failed read: no reading before a good one, the last value stale after one | `LibreSourceTests.ANullValue_IsAFailedRead_NeverOneFourXsZero` |
| Removed hardware stayed live: `LibreMonitor`'s `SENSORHASH` kept every sensor it had seen, reading its last value as live | A sensor gone from the helper's catalogue is stale, then unavailable after 30 s, then unlisted | `LibreSourceTests.RemovedHardware_GoesStale_ThenUnavailable_ThenUnlisted_NeverLiveAsInOneFourX` |
