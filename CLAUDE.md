# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

Branch `2.0.x` is InfoPanel 2.0: a cross-platform .NET engine (Windows + macOS) with a web editor. 1.x ships from `1.4.x`. Decisions for 2.0 are recorded in `docs/2.0/decisions.md`.

- **SDK:** .NET 10, pinned by `global.json` (`10.0.403`, `rollForward: latestFeature`).
- **Solution:** `InfoPanel.slnx`, Debug/Release, Any CPU. No platform or runtime identifier is forced; builds run on macOS (arm64) and Windows.
- **Package versions** live in `Directory.Packages.props` (central package management). A `PackageReference` never carries a `Version`. There are no `packages.lock.json` files.
- **Plugin SDK projects** (`InfoPanel.Plugins*`) target net8.0 so existing plugins keep loading. `InfoPanel.Extras` targets net8.0-windows and builds anywhere via `EnableWindowsTargeting`. Core and tests target net10.0.
- The 1.x WPF app is gone from this branch; `PORTING.md` lists what it did and where each part goes in 2.0.

```bash
dotnet build InfoPanel.slnx            # -c Release for Release
dotnet test InfoPanel.slnx             # xUnit v3, run through VSTest
dotnet test InfoPanel.slnx --filter FullyQualifiedName~SensorKey

# Run plugin simulator for testing plugins
dotnet run --project InfoPanel.Plugins.Simulator/InfoPanel.Plugins.Simulator.csproj
```

## Architecture (2.0.x)

2.0 is one headless .NET engine that loads profiles, reads sensors, renders frames with SkiaSharp and serves the editor to the user's browser on localhost. The preview the editor shows is rendered by the engine with the same code that drives panels; a client draws only its own overlays, from the item bounds. The engine host (`InfoPanel`, ASP.NET Core) arrives in M4; today the branch has the renderer library, its tests and the plugin SDK.

| Project | Role |
|---|---|
| **InfoPanel.Core** (net10.0) | The 1.x model (14 display item kinds + `Profile`) and renderer, moved from the WPF app with history, behind cross-platform seams |
| **InfoPanel.Core.Tests** | xUnit v3 tests for Core |
| **InfoPanel.Plugins**, **.Plugins.Graphics**, **.Plugins.Ipc**, **.Plugins.Host**, **.Plugins.Loader**, **.Plugins.Simulator** (net8.0) | Plugin SDK, out-of-process plugin host and simulator (see `PLUGINS.md`, `PLUGIN-ARCHITECTURE.md`) |
| **InfoPanel.Extras** | Built-in plugins (Windows-only at runtime) |

### InfoPanel.Core

Root namespace `InfoPanel`. Files moved from 1.x keep their 1.x namespaces (`InfoPanel.Models`, `InfoPanel.Drawing`, ...).

- `Models/` — `DisplayItem` and the 14 kinds (Text, Sensor, Clock, Calendar, Table, Image, SensorImage, HttpImage, Gauge, Graph, Bar, Donut, Shape, Group), `Profile`, `SensorReading`. These are the 1.x XML types.
- `Sensors/` — `SensorKey` (`hwinfo:{remote}:{id}:{instance}:{entry}`, `libre:{id}`, `plugin:{id}`), `ISensorReader`, `ISensorSource`, `ISensorHistory`, `SensorKeys` (maps an item's sensor binding to a key).
- `Images/` — `IImageSource` (non-blocking; null means "draw the placeholder"), `IRenderImage`, `IAssetLocator`, `IMediaPlayback`; `ImageCache` (local raster and SVG files; null for HTTP, RTSP, video and `plugin-image://` until providers exist) and `LockedImage`.
- `Drawing/` — `RenderContext` (sensors, history, images, assets, `ItemStateStore`, `Tick`, `Now`, `Elapsed`, `RenderOptions`), `PanelDraw.Run(profile, items, graphics, ctx)`, `GraphDraw`, `SkiaGraphics` (all three internal), `FontDefaults`.
- `Editing/UndoHistory.cs` — the 1.x undo manager, unchanged; rewritten per document in M5.

Rendering reads nothing global: time, sensor values, history, images and per-item animation state (gauge frames, chart smoothing) all come from the `RenderContext`, so the same inputs give the same pixels. Animation state advances at most once per `Tick`. Selection, grid lines and other editor chrome are never drawn by Core.

## Invariants

- **The engine owns the model.** The editor sends patches only (property sets, structural operations) and never round-trips whole profiles or items through its own model. Undo/redo and dirty state live in the engine.
- **1.x XML compatibility.** Profiles are `profiles.xml` + `profiles/{guid}.xml`, written with `XmlSerializer`, and 1.x must keep reading what 2.0 writes:
  - Model freeze: never add, remove or rename a public read/write property or public field on `Profile` or any display item type, and never change the type of one that serializes. `[ObservableProperty]` fields count (it does not copy `[XmlIgnore]`, so those serialize). Members that do not serialize (get-only, `[XmlIgnore]`) may change.
  - `HttpImageDisplayItem.HttpUrl` is kept but always serializes as null: the one whitelisted difference from 1.x output.
  - `DisplayItem.Guid` is still `[XmlIgnore]`; M2 serializes it as a `<Guid>` element (1.x ignores unknown elements). That is the one planned model change.
- **Goldens and snapshots** (from M3 and M5): rendering changes are guarded by golden images and the kind manifest by a snapshot. Regenerate only for an intended change, and review the diff:
  - `UPDATE_GOLDENS=1 dotnet test --filter Category=Golden`
  - `UPDATE_SNAPSHOTS=1 dotnet test --filter KindManifest`
- **1.x code** is read from the `1.4.x` branch with `git show 1.4.x:<path>` (no worktrees). Port from there, using `PORTING.md` as the backlog.
- **No CI on 2.0.x** for now; `.github/workflows/dotnet-desktop.yml` is the 1.x workflow and is left untouched.

## Related Documentation

- `docs/2.0/decisions.md` — 2.0 decisions (date, decision, reason)
- `PORTING.md` — 1.x components still to port, with their 1.4.x sources
- `PLUGINS.md` — Plugin development guide (for plugin authors)
- `PLUGIN-ARCHITECTURE.md` — Plugin system internals (for contributors; describes the 1.x host side)
- `PANELS.md` — Hardware panel support and models (1.x)
- `LoggingGuidelines.md` — Serilog usage standards and log levels
