# Porting backlog: 1.x → 2.0

`2.0.x` removed the 1.x WPF app (`InfoPanel/`, `InfoPanel.Views.Skia/`, `InnoSetup/`). The portable model and renderer moved to `InfoPanel.Core` with history (`git log --follow`). Everything else is listed here with where it goes in 2.0. Read the 1.x source with `git show 1.4.x:<path>` (for example `git show 1.4.x:InfoPanel/HWHash/HWHash.cs`).

The 2.0 targets are the seams in `InfoPanel.Core` (`ISensorSource`, `IImageSource`, `IMediaPlayback`) and the ones that arrive later (`IFrameSink` in M6).

## Sensors

| 1.4.x component | 2.0 target | Source (`git show 1.4.x:<path>`) |
|---|---|---|
| HWHash (HWiNFO shared memory) | `ISensorSource` | `InfoPanel/HWHash/HWHash.cs` |
| LibreMonitor | `ISensorSource` | `InfoPanel/Monitors/LibreMonitor.cs` |
| PluginMonitor + proxies | `ISensorSource`, plus `IImageSource` for `plugin-image://` | `InfoPanel/Monitors/PluginMonitor.cs`, `InfoPanel/Monitors/PluginHostConnection.cs`, `InfoPanel/Monitors/PluginProcessManager.cs`, `InfoPanel/Monitors/PluginProxies/*.cs` |
| SensorReader (static reads, missing reading → null) | `ISensorReader` / sensor hub (M4); report unavailable, stale and permission-denied as distinct states later | `InfoPanel/Models/SensorReader.cs` |
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
| DisplayWindow*, `DisplayWindowManager`, ForegroundAppMonitor, ScreenHelper | overlay `IFrameSink` (native companion) | `InfoPanel/Views/DisplayWindow.xaml.cs`, `InfoPanel/Views/SkiaDisplayWindow.xaml.cs`, `InfoPanel/DisplayWindowManager.cs`, `InfoPanel/DisplayWindowThread.cs`, `InfoPanel/Services/ForegroundAppMonitor.cs`, `InfoPanel/Utils/ForegroundWindowHelper.cs`, `InfoPanel/Utils/ScreenHelper.cs` |
| Selection blink, grid lines and the selected-item colour drawn into outputs (`Settings.ShowGridLines`, `GridLinesSpacing`, `GridLinesColor`, `SelectedItemColor`) | Not ported to Core: engine output has no selection or grid lines | `InfoPanel/Drawing/PanelDraw.cs`, `InfoPanel/Models/Settings.cs` |
| `ProfilePreviewCoordinator` (batched thumbnails) | `RenderService.RenderOnce` (M6) and `/thumbnail` (M4) | `InfoPanel/Services/ProfilePreviewCoordinator.cs` |

## Persistence and editing

| 1.4.x component | 2.0 target | Source |
|---|---|---|
| `SharedModel` item XML load/save, `.infopanel` import/export | `ProfileSerializer`, `ProfileStore`, `InfoPanelArchive` (M2) | `InfoPanel/Models/SharedModel.cs` |
| `ConfigModel` profile list | `ProfileStore` (M2) | `InfoPanel/Models/ConfigModel.cs` |
| `UndoManager` | `UndoHistory` rewrite per document (M5); the moved file compiles unchanged in `InfoPanel.Core/Editing/UndoHistory.cs` | `InfoPanel/Services/UndoManager.cs` |
| 1.4.x model changes after `8a3062d`: `GuideDisplayItem` (15th item type, editor alignment guides; dropped on read today) and `ImageDisplayItem.RefreshInterval` (ignored on read) | sync into the Core models and the kind manifest, later | `InfoPanel/Models/GuideDisplayItem.cs`, `InfoPanel/Models/ImageDisplayItem.cs` |
| `FontCache` (system font list for pickers) | `FontRegistry` + `/api/fonts` (M3/M4) | `InfoPanel/Utils/FontCache.cs` |

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
| ConfigModel / Settings (app settings) | later | `InfoPanel/Models/ConfigModel.cs`, `InfoPanel/Models/Settings.cs` |
| PawnIO driver setup | later (Windows adapter) | `InfoPanel/Utils/PawnIoHelper.cs`, `InfoPanel/Resources/PawnIO_setup.exe` |
| JobObject (plugin host lifetime) | later (plugin host on macOS) | `InfoPanel/Utils/JobObject.cs` |
| Installer | later | `InnoSetup/setup.iss` |
