# Porting backlog: 1.x → 2.0

`2.0.x` removed the 1.x WPF app (`InfoPanel/`, `InfoPanel.Views.Skia/`, `InnoSetup/`). The portable model and renderer moved to `InfoPanel.Core` with history (`git log --follow`). Everything else is listed here with where it goes in 2.0. Read the 1.x source with `git show 1.4.x:<path>` (for example `git show 1.4.x:InfoPanel/HWHash/HWHash.cs`).

The 2.0 targets are the seams in `InfoPanel.Core` (`ISensorSource`, `IImageSource`, `IMediaPlayback`) and the ones that arrive later (`IFrameSink` in M6).

## Sensors

| 1.4.x component | 2.0 target | Source (`git show 1.4.x:<path>`) |
|---|---|---|
| HWHash (HWiNFO shared memory) | `ISensorSource` | `InfoPanel/HWHash/HWHash.cs` |
| LibreMonitor | `ISensorSource` | `InfoPanel/Monitors/LibreMonitor.cs` |
| PluginMonitor + proxies | `ISensorSource`, plus `IImageSource` for `plugin-image://` | `InfoPanel/Monitors/PluginMonitor.cs`, `InfoPanel/Monitors/PluginHostConnection.cs`, `InfoPanel/Monitors/PluginProcessManager.cs`, `InfoPanel/Monitors/PluginProxies/*.cs` |
| SensorReader (static reads, missing reading → null) | `ISensorReader` / sensor hub (M4); stale, unavailable and denied are distinct statuses (done, `SensorStatus`); Windows sources build on `PolledSensorSource` | `InfoPanel/Models/SensorReader.cs` |
| GraphDraw's lazy history queues (sampled at `TargetGraphUpdateRate`) | `ISensorHistory` service (M4) | `InfoPanel/Drawing/GraphDraw.cs` |
| HWiNFO smart import (remap sensors by name) | `ISensorSource.TryResolveByName`, later | `InfoPanel/Models/SharedModel.cs` (~l.900, "Smart imported") |

## Images and video

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| `LockedImage` FFMPEG branch (Flyleaf player, snapshots via WPF `BitmapSource`), `VideoBackgroundHelper`, the `ImageDisplayItem.Volume` handling | video image provider + `IMediaPlayback` (headless decoder) | `InfoPanel/Models/LockedImage.cs`, `InfoPanel/Models/VideoBackgroundHelper.cs` |
| `LockedImage` HTTP branch (download on load) | HTTP image provider behind `IImageSource`; `ImageCache` returns null for http(s) today, so HTTP and HttpImage items draw the placeholder | `InfoPanel/Models/LockedImage.cs` |
| `LockedImage` PLUGIN branch, `Cache.GetPluginImage` / `InvalidatePluginImages` | `IImageSource` for `plugin-image://` (the named `MemoryMappedFile` transport is Windows-only) | `InfoPanel/Models/LockedImage.cs`, `InfoPanel/Utils/Cache.cs`, `InfoPanel/Monitors/PluginProxies/ProxyPluginImage.cs` |
| `LockedImage` GPU texture frame cache (`GRContext`, dispatcher disposal) | GPU-backed renderer, if ever needed | `InfoPanel/Models/LockedImage.cs`, `InfoPanel/Drawing/AcceleratedGraphics.cs` |
| `Cache.InvalidateImage` on URL edits | `ImageCache.Invalidate` called by the editing API / asset upload (M5 and later) | `InfoPanel/Utils/Cache.cs`, `InfoPanel/Models/ImageDisplayItem.cs` |

## Output: panels and overlays

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| BeadaPanel, Turing, Thermalright and Thermaltake tasks and protocols | `IFrameSink` panels | `InfoPanel/Services/{BeadaPanel,BeadaPanelDevice,TuringPanel,TuringPanelSerial,TuringPanelUsbDevice,ThermalrightPanel,ThermalrightPanelDevice,ThermaltakePanel,ThermaltakePanelDevice}Task.cs`, `InfoPanel/BeadaPanel/`, `InfoPanel/TuringPanel/`, `InfoPanel/ThermalrightPanel/`, `InfoPanel/ThermaltakePanel/`, `InfoPanel/Models/*PanelDevice.cs`, `InfoPanel/Lib/` (Turing DLLs) |
| `SKBitmapExtensions.EnsureBitmapSize` and the LCD rotation helpers (`GetRotatedDimensions`, `GetRotationAngle`, `LCD_ROTATION`) | `IFrameSink` panels (`FrameRequest` gains `PanelRotation`) | `InfoPanel/Extensions/SKBitmapExtensions.cs`, `InfoPanel/ViewModels/UsbPanelsViewModel.cs` (`LCD_ROTATION`), `InfoPanel/Enums/Rotation.cs` |
| `SKBitmapComparison` (skip unchanged panel frames) | `IFrameSink` panels (content hash) | `InfoPanel/Extensions/SKBitmapComparison.cs` |
| `PanelDrawTask` (per-panel render loop) | `RenderLoop` (M6) | `InfoPanel/Services/PanelDrawTask.cs` |
| DisplayWindow*, `DisplayWindowManager`, ForegroundAppMonitor, ScreenHelper | macOS done: `InfoPanel/Desktop/` (`DisplayWindowManager`, an `IFrameSink` per active profile, AppKit in `Desktop/MacOS/`). Still to port: the Windows `IDesktopPlatform` (each display's device name as its `name`, the name Windows shows people as its `label`) and ForegroundAppMonitor (`TriggerProcessNames`). The profile page's display picker and `Fullscreen()` are a client's now: `GET /api/displays` lists the displays, each in the shape `window.display` takes, and filling one is a single `profile` commit (`spec/docs/api.md`, "Displays") | `InfoPanel/Views/DisplayWindow.xaml.cs`, `InfoPanel/Views/SkiaDisplayWindow.xaml.cs`, `InfoPanel/DisplayWindowManager.cs`, `InfoPanel/DisplayWindowThread.cs`, `InfoPanel/Services/ForegroundAppMonitor.cs`, `InfoPanel/Utils/ForegroundWindowHelper.cs`, `InfoPanel/Utils/ScreenHelper.cs` |
| Selection blink, grid lines and the selected-item colour drawn into outputs (`Settings.ShowGridLines`, `GridLinesSpacing`, `GridLinesColor`, `SelectedItemColor`) | Not ported to Core: engine output has no selection or grid lines | `InfoPanel/Drawing/PanelDraw.cs`, `InfoPanel/Models/Settings.cs` |
| `ProfilePreviewCoordinator` (batched thumbnails) | `RenderService.RenderOnce` (M6) and `/thumbnail` (M4) | `InfoPanel/Services/ProfilePreviewCoordinator.cs` |

## Persistence and editing

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| `SharedModel` item XML load/save, `.infopanel` import/export | Read: `ProfileSerializer` (through `SafeXml`); `.infopanel` import and the 1.x folder: the 1.x import (`InfoPanel.Core/Import/`: `V1Reader`, `V1Folder`, `ArchiveReader`), which reads into an envelope through `/api/import/v1` (spec `docs/import.md`) and installs nothing; a client creates the profiles. `InfoPanelArchive.Export` writes only the e2e archive. Export: a 2.0 profile as a profile archive, `.infopanel2` (`ProfileArchive`, `GET /api/profiles/{id}/archive`, spec `docs/archive.md`), named by 1.4.x's file-name rule, opened again with `POST /api/import/archive` | `InfoPanel/Models/SharedModel.cs` |
| `ConfigModel` profile list and settings (`settings.xml`, its `.bak` fallback) | Read by the 1.x import (`V1Folder`, `V1SettingsReader`: the profile list, the settings' user data); applying the settings is the settings and panel tasks' | `InfoPanel/Models/ConfigModel.cs`, `InfoPanel/Models/Settings.cs` |
| `UndoManager` | Done (M5): `UndoHistory` per `ProfileDocument` (profile + items snapshots, coalescing by the edit's key) | `InfoPanel/Services/UndoManager.cs` |
| 1.4.x model changes after `8a3062d`: `GuideDisplayItem` (15th item type, editor alignment guides) and `ImageDisplayItem.RefreshInterval` | Guides: read from the raw XML by the 1.x import (the envelope's `guides`); `RefreshInterval`: reported (`import.element`) and ignored. The model sync is needed only for the refresh behaviour, with the HTTP image provider | `InfoPanel/Models/GuideDisplayItem.cs`, `InfoPanel/Models/ImageDisplayItem.cs` |
| `FontCache` (system font list for pickers) | `FontRegistry` + `/api/fonts` (M3/M4) | `InfoPanel/Utils/FontCache.cs` |

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

The host side runs on `2.0.x` (net8.0, not loaded by the engine yet); these two touch the app side, which comes over with `PluginMonitor` and the `plugin-image://` source (see the Sensors and Images tables above).

| Commit | What it does |
|---|---|
| `48b3684` | Fix: plugin actions are invoked through the current host connection, so the action buttons keep working after the host restarts (`PluginsViewModel`; port the idea into the `ISensorSource` that replaces `PluginMonitor`) |
| `1acc815` | Fix: large plugin images (2650x720) starved the window's input under heavy render load. Adds a no-resize fast path for plugin frames in `LockedImage` (port with the `plugin-image://` source); the `DisplayWindow` dispatcher-priority part is WPF only |

### Guide and image-refresh model fields

The 1.x import already reads guides from the raw XML and reports `RefreshInterval`; what remains is the refresh behaviour, which needs an HTTP image provider behind `IImageSource`.

| Commit | What it does |
|---|---|
| `4c82f71` | `GuideDisplayItem`, a 15th item kind for editor alignment guides (the same commit's editor changes are WPF only; `2.0.x` documents already have `Guides`) |
| `1114dac` | `ImageDisplayItem.RefreshInterval`: re-download URL images every N seconds, with `LockedImage.LoadedAtUtc` and the cache logic in `Utils/Cache.cs` |

## Later

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| WebServerTask (HTTP API, `/{id}/image`) | engine host, later | `InfoPanel/Services/WebServerTask.cs` |
| Global hotkeys | native companion, later | `InfoPanel/Services/GlobalHotkeyService.cs`, `InfoPanel/Models/HotkeyBinding.cs` |
| Tray icon | native companion, later | `InfoPanel/Views/Windows/MainWindow.xaml`, `InfoPanel/Views/Windows/MainWindow.xaml.cs` |
| Autostart (Task Scheduler) | native companion, later | `InfoPanel/Models/ConfigModel.cs` |
| UpdateChecker and toasts | later | `InfoPanel/Services/UpdateChecker.cs` |
| Sentry | later | `InfoPanel/App.xaml.cs` |
| AIDA64 SensorPanel import | later | `InfoPanel/Models/SharedModel.SensorPanelImport.cs` |
| ConfigModel / Settings (app settings) | the engine settings (`SettingsStore`, `GET`/`PATCH /api/settings`) hold the frame rate, graph rate, log level and the Libre source's settings; the devices', tray's, autostart's, hotkeys', autosave's and web server's settings later, each with its feature | `InfoPanel/Models/ConfigModel.cs`, `InfoPanel/Models/Settings.cs` |
| PawnIO driver setup | later (Windows adapter) | `InfoPanel/Utils/PawnIoHelper.cs`, `InfoPanel/Resources/PawnIO_setup.exe` |
| JobObject (plugin host lifetime) | later (plugin host on macOS) | `InfoPanel/Utils/JobObject.cs` |
| Installer | later | `InnoSetup/setup.iss` |
